#nullable enable

using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using MajMusic.Core.Models;
using MajMusic.Presentation.ViewModels;
using TKCode.Arc;

namespace MajMusic.Presentation.Views
{
    /// <summary>Displays comments and available Simai difficulties in the same animated tab strip.</summary>
    public sealed class SongPreviewTabs : SegmentedControl
    {
        /// <summary>Enables the details tab used by the compact song sheet.</summary>
        public static readonly StyledProperty<bool> ShowOverviewProperty =
            AvaloniaProperty.Register<SongPreviewTabs, bool>(nameof(ShowOverview));

        private static readonly string[] ShortNames = ["EZ", "B", "A", "EX", "M", "Re", "宴"];
        private static readonly string[] FullNames = ["Easy", "Basic", "Advanced", "Expert", "Master", "Re:Master", "UTAGE"];
        private MainViewModel? _owner;
        private IReadOnlyList<ChartPreviewDifficulty>? _difficulties;
        private bool _lastOverview;
        private bool _lastComments;
        private bool _attached;
        private bool _synchronizing;
        private bool _preferOverview;

        /// <summary>Creates a single row with equal tabs and Arc's sliding selection indicator.</summary>
        public SongPreviewTabs()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Center;
            ItemsPanel = new FuncTemplate<Panel?>(() => new UniformGrid { Rows = 1 });
            SelectionChanged += OnTabSelected;
            DataContextChanged += (_, _) =>
            {
                if (_attached)
                {
                    ObserveOwner(DataContext as MainViewModel);
                }
            };
        }

        /// <summary>Gets or sets whether the strip includes the overview and hides unavailable comments.</summary>
        public bool ShowOverview
        {
            get => GetValue(ShowOverviewProperty);
            set => SetValue(ShowOverviewProperty, value);
        }

        /// <inheritdoc />
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
        {
            base.OnAttachedToVisualTree(args);
            _attached = true;
            ObserveOwner(DataContext as MainViewModel);
        }

        /// <inheritdoc />
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
        {
            _attached = false;
            ObserveOwner(null);
            base.OnDetachedFromVisualTree(args);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == ShowOverviewProperty && _attached)
            {
                Synchronize();
            }
        }

        /// <inheritdoc />
        protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
        {
            base.PrepareContainerForItemOverride(container, item, index);
            if (container is ContentControl segment && item is PreviewOption option)
            {
                segment.Padding = new Thickness(2, 3);
                segment.FontSize = 12;
                ToolTip.SetTip(segment, option.FullLabel);
                AutomationProperties.SetName(segment, option.FullLabel);
            }
        }

        private void ObserveOwner(MainViewModel? owner)
        {
            if (_owner is not null)
            {
                _owner.PropertyChanged -= OnOwnerChanged;
            }
            _owner = owner;
            _preferOverview = false;
            if (owner is not null)
            {
                owner.PropertyChanged += OnOwnerChanged;
            }
            Synchronize();
        }

        private void OnOwnerChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(MainViewModel.IsSongDetailsExpanded))
            {
                _preferOverview = false;
            }
            if (args.PropertyName is nameof(MainViewModel.IsSongDetailsExpanded)
                or nameof(MainViewModel.IsSongSocialVisible) or nameof(MainViewModel.IsSongChartPreviewSelected)
                or nameof(MainViewModel.MajViewDifficulties) or nameof(MainViewModel.SelectedMajViewDifficulty))
            {
                Synchronize();
            }
        }

        private void Synchronize()
        {
            var owner = _owner;
            var difficulties = owner?.MajViewDifficulties;
            var showComments = !ShowOverview || owner?.IsSongSocialVisible == true;
            int? selectedValue = owner?.IsSongChartPreviewSelected == true
                ? difficulties?.Any(item => item.Number == owner.SelectedMajViewDifficulty) == true
                    ? owner.SelectedMajViewDifficulty : null
                : ShowOverview && (_preferOverview || !showComments) ? -1 : 0;
            _synchronizing = true;
            try
            {
                if (ItemsSource is null || !ReferenceEquals(_difficulties, difficulties) || _lastOverview != ShowOverview
                    || _lastComments != showComments)
                {
                    _difficulties = difficulties;
                    _lastOverview = ShowOverview;
                    _lastComments = showComments;
                    List<PreviewOption> tabs = new();
                    if (ShowOverview)
                    {
                        tabs.Add(new PreviewOption(-1, "详情", "详情"));
                    }
                    if (showComments)
                    {
                        tabs.Add(new PreviewOption(0, "评论", "评论"));
                    }
                    if (difficulties is not null)
                    {
                        foreach (var difficulty in difficulties)
                        {
                            if (difficulty.Number is not (>= 1 and <= 7))
                            {
                                continue;
                            }
                            var name = ShortNames[difficulty.Number - 1];
                            var level = difficulty.Level?.Trim();
                            var label = level is { Length: > 0 and <= 3 } && (!ShowOverview || difficulties.Count < 6)
                                ? $"{name} {level}" : name;
                            tabs.Add(new PreviewOption(difficulty.Number, label, $"难度 {difficulty.Number} · {FullNames[difficulty.Number - 1]} {level}".TrimEnd()));
                        }
                    }
                    ItemsSource = tabs;
                }
                SetCurrentValue(SelectedValueProperty, selectedValue);
            }
            finally
            {
                _synchronizing = false;
            }
        }

        private async void OnTabSelected(object? sender, SelectionChangedEventArgs args)
        {
            if (_synchronizing || !ReferenceEquals(args.Source, this) || SelectedValue is not int slot
                || _owner is not { IsSongDetailsExpanded: true } owner)
            {
                return;
            }
            _preferOverview = slot == -1;
            if (slot > 0)
            {
                await owner.SelectMajViewDifficultyAsync(slot);
            }
            else
            {
                owner.IsSongChartPreviewSelected = false;
            }
        }

        private sealed class PreviewOption(int slot, string label, string fullLabel) : SegmentedOption(slot, label)
        {
            public string FullLabel { get; } = fullLabel;
        }
    }
}
