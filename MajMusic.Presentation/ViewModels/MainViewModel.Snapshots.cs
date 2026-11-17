#nullable enable

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MajMusic.Presentation.ViewModels
{
    /// <summary>Publishes complete query and virtualized-row snapshots to the shared presentation.</summary>
    public sealed partial class MainViewModel
    {
        /// <summary>Publishes one complete library or card-row snapshot to bound controls.</summary>
        /// <typeparam name="T">The immutable item or stable row presentation.</typeparam>
        internal sealed class SnapshotCollection<T> : ObservableCollection<T>
        {
            /// <summary>Resizes an unchanged prefix with one range notification for its added or removed tail.</summary>
            /// <param name="items">The complete snapshot whose shared prefix already matches the current items.</param>
            public void ResizeMatchingPrefix(IReadOnlyList<T> items)
            {
                CheckReentrancy();
                var retained = Math.Min(Count, items.Count);
                if (Count > retained)
                {
                    var removed = new List<T>(Count - retained);
                    for (var index = retained; index < Count; index++)
                    {
                        removed.Add(Items[index]);
                    }
                    while (Items.Count > retained)
                    {
                        Items.RemoveAt(Items.Count - 1);
                    }
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                    OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Remove, removed, retained));
                }
                else if (items.Count > retained)
                {
                    var added = new List<T>(items.Count - retained);
                    for (var index = retained; index < items.Count; index++)
                    {
                        Items.Add(items[index]);
                        added.Add(items[index]);
                    }
                    OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                    OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                    OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, added, retained));
                }
            }

            /// <summary>Replaces the underlying items and raises one reset after the snapshot is complete.</summary>
            /// <param name="items">The next complete snapshot in display order.</param>
            public void ReplaceAll(IReadOnlyList<T> items)
            {
                CheckReentrancy();
                Items.Clear();
                foreach (var item in items)
                {
                    Items.Add(item);
                }

                OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
                OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
                OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
            }
        }
    }
}
