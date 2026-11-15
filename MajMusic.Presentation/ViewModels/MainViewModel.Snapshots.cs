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
        private sealed class SnapshotCollection<T> : ObservableCollection<T>
        {
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
