#nullable enable

using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using MajMusic.Core.Models;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Keeps each virtualized music-row container's real card controls when native list recycling clears its item.</summary>
    public sealed class BrowseCardListBox : ListBox
    {
        /// <summary>Identifies the density inherited by current and newly recycled music rows.</summary>
        public static readonly StyledProperty<SongCardLayout> CardLayoutProperty =
            AvaloniaProperty.Register<BrowseCardListBox, SongCardLayout>(nameof(CardLayout));
        /// <summary>Separates persistent song-row containers from ordinary pagination and other list items.</summary>
        private static readonly object SongRowRecycleKey = new();

        /// <summary>Gets or sets the shared proportions used by current and reused song cards.</summary>
        public SongCardLayout CardLayout
        {
            get => GetValue(CardLayoutProperty);
            set => SetValue(CardLayoutProperty, value);
        }

        /// <inheritdoc />
        protected override Type StyleKeyOverride => typeof(ListBox);

        /// <inheritdoc />
        protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey)
        {
            if (item is SongCardRow)
            {
                recycleKey = SongRowRecycleKey;
                return true;
            }
            return base.NeedsContainerOverride(item, index, out recycleKey);
        }

        /// <inheritdoc />
        protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey)
        {
            return ReferenceEquals(recycleKey, SongRowRecycleKey) ? new SongRowContainer()
                : base.CreateContainerForItemOverride(item, index, recycleKey);
        }

        /// <inheritdoc />
        protected override void PrepareContainerForItemOverride(Control container, object? item, int index)
        {
            if (container is SongRowContainer music && item is SongCardRow row)
            {
                music.Row.Columns = row.Columns;
                music.Row.CardLayout = CardLayout;
                base.PrepareContainerForItemOverride(container, item, index);
                music.Row.IsBrowsingActive = IsVisible;
            }
            else
            {
                base.PrepareContainerForItemOverride(container, item, index);
            }
        }

        /// <inheritdoc />
        protected override void ClearContainerForItemOverride(Control container)
        {
            if (container is SongRowContainer music)
            {
                music.Row.IsBrowsingActive = false;
                music.DataContext = null;
            }
            // The base selection implementation must keep its own suppression guard while clearing IsSelected.
            base.ClearContainerForItemOverride(container);
        }

        /// <inheritdoc />
        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == CardLayoutProperty || change.Property == IsVisibleProperty)
            {
                foreach (var container in GetRealizedContainers())
                {
                    if (container is SongRowContainer music)
                    {
                        music.Row.CardLayout = CardLayout;
                        music.Row.IsBrowsingActive = IsVisible;
                    }
                }
            }
        }

        /// <summary>Retains exactly one row panel through the native generator's local Content clearing operation.</summary>
        private sealed class SongRowContainer : ListBoxItem
        {
            /// <summary>Installs fixed template-priority content while allowing the standard container lifecycle to run.</summary>
            public SongRowContainer()
            {
                Row = new SongCardRowView { IsBrowsingActive = false };
                // ItemsControl clears local Content during recycling. This lower-priority fixed child survives that clear.
                SetValue(ContentProperty, Row, BindingPriority.Template);
            }

            /// <summary>Gets the original lightweight panel and its bounded reusable song-control prefix.</summary>
            public SongCardRowView Row { get; }
            /// <inheritdoc />
            protected override Type StyleKeyOverride => typeof(ListBoxItem);
        }
    }
}
