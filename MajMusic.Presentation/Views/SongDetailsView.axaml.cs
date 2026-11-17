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
        /// <summary>Creates the expanded content without requesting social data or starting playback.</summary>
        public SongDetailsView()
        {
            InitializeComponent();
            _initialized = true;
            DataContextChanged += OnOwnerChanged;
            SizeChanged += OnDetailsSizeChanged;
            UpdateSummaryVisibility();
            UpdateViewportChrome();
        }

        /// <summary>Gives discussions and the chart area the available height while metadata retains its artwork summary.</summary>
        /// <param name="sender">The detail content tabs.</param>
        /// <param name="e">The selected detail pane.</param>
        private void OnDetailsTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (_initialized && sender is TabControl tabs && ReferenceEquals(e.Source, tabs))
            {
                UpdateSummaryVisibility();
                UpdateViewportChrome();
            }
        }

        /// <summary>Synchronizes the artwork row only after the view's named controls exist.</summary>
        private void UpdateSummaryVisibility()
        {
            if (_initialized && this.FindControl<TabControl>("SongDetailsTabs") is { } tabs
                && this.FindControl<Grid>("SongDetailsSummary") is { } summary)
            {
                summary.IsVisible = tabs.SelectedIndex == 0;
            }
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
            ResetUnavailableComments();
            if (_initialized && _owner is { IsSongDetailsExpanded: true, IsSongSocialVisible: true })
            {
                this.FindControl<TabControl>("SongDetailsTabs")!.SelectedIndex = 1;
            }
            UpdateSummaryVisibility();
            UpdateViewportChrome();
        }

        /// <summary>Returns to ordinary metadata when social actions stop belonging to the selected song.</summary>
        /// <param name="sender">The current workspace.</param>
        /// <param name="e">The changed social or selection property.</param>
        private void OnSocialAvailabilityChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.IsSongDetailsExpanded) && _owner is { IsSongDetailsExpanded: true, IsSongSocialVisible: true })
            {
                this.FindControl<TabControl>("SongDetailsTabs")!.SelectedIndex = 1;
            }
            if (e.PropertyName is nameof(MainViewModel.IsSongSocialVisible) or nameof(MainViewModel.SelectedTrack))
            {
                ResetUnavailableComments();
            }
            if (e.PropertyName is nameof(MainViewModel.HasSongReplyTarget) or nameof(MainViewModel.HasSongSocialStatus))
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
            if (!_initialized || this.FindControl<TabControl>("SongDetailsTabs") is not { } tabs)
            {
                return;
            }
            var threshold = 460 + (_owner?.HasSongReplyTarget == true ? 44 : 0) + (_owner?.HasSongSocialStatus == true ? 28 : 0);
            var compact = tabs.SelectedIndex == 1 && Bounds.Height > 0 && Bounds.Height < threshold;
            if (_chromeInitialized && compact == _compactComments)
            {
                return;
            }
            _chromeInitialized = true;
            _compactComments = compact;
            var layout = this.FindControl<Grid>("SongDetailsLayout")!;
            layout.Margin = compact ? new Thickness(16, 4) : new Thickness(16);
            layout.RowSpacing = compact ? 3 : 12;
            var comments = this.FindControl<Grid>("SongCommentsLayout")!;
            comments.Margin = new Thickness(0, compact ? 3 : 8, 0, 0);
            comments.RowSpacing = compact ? 3 : 8;
            this.FindControl<Grid>("SongCommentsHeader")!.IsVisible = !compact;
            this.FindControl<Grid>("SongMoreCommentsHost")!.IsVisible = !compact;
            this.FindControl<Grid>("SongDetailsFooter")!.IsVisible = !compact;
            this.FindControl<StackPanel>("CompactSongDetailsActions")!.IsVisible = compact;
            this.FindControl<TextBlock>("SongCommentsStatus")!.MaxLines = compact ? 1 : 2;
        }

        /// <summary>Closes an unavailable comment pane without changing song selection or issuing requests.</summary>
        private void ResetUnavailableComments()
        {
            if (_initialized && _owner?.IsSongSocialVisible != true && this.FindControl<TabControl>("SongDetailsTabs") is { SelectedIndex: 1 } tabs)
            {
                tabs.SelectedIndex = 0;
            }
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
