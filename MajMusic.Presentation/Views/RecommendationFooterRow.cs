#nullable enable

using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MajMusic.Presentation.ViewModels;

namespace MajMusic.Presentation.Views
{
    /// <summary>Identifies the nonmusic row displayed after a complete recommendation page.</summary>
    public sealed class RecommendationFooterRow
    {
        /// <summary>Creates a footer using the same pagination commands as its music browser.</summary>
        /// <param name="owner">The current browsing presentation.</param>
        public RecommendationFooterRow(MainViewModel owner)
        {
            Owner = owner;
        }

        /// <summary>Gets the owner of the current page choices and their commands.</summary>
        public MainViewModel Owner { get; }
    }

    /// <summary>Adds one optional trailing footer without copying or materializing the underlying music rows.</summary>
    internal sealed class BrowseRowsPresentation : IList, INotifyCollectionChanged, IDisposable
    {
        /// <summary>References lightweight source rows independently of their lazily constructed cards.</summary>
        private readonly ObservableCollection<SongCardRow> _rows;
        /// <summary>Retains the single stable nonmusic row.</summary>
        private readonly RecommendationFooterRow _footer;
        /// <summary>Indicates whether the complete current recommendation page has a trailing footer.</summary>
        private bool _showFooter;

        /// <summary>Creates a live, indexed source with no additional music-row allocations.</summary>
        /// <param name="owner">The current music presentation.</param>
        public BrowseRowsPresentation(MainViewModel owner)
        {
            _rows = owner.BrowseRows;
            _footer = new RecommendationFooterRow(owner);
            _rows.CollectionChanged += OnRowsChanged;
        }

        /// <inheritdoc />
        public event NotifyCollectionChangedEventHandler? CollectionChanged;
        /// <inheritdoc />
        public int Count => _rows.Count + (_showFooter ? 1 : 0);
        /// <summary>Gets the currently available footer, or null while music or artwork is still entering.</summary>
        public RecommendationFooterRow? Footer => _showFooter ? _footer : null;
        /// <inheritdoc />
        public bool IsFixedSize => true;
        /// <inheritdoc />
        public bool IsReadOnly => true;
        /// <inheritdoc />
        public bool IsSynchronized => false;
        /// <inheritdoc />
        public object SyncRoot => this;
        /// <inheritdoc />
        public object? this[int index]
        {
            get => index >= 0 && index < _rows.Count ? _rows[index]
                : _showFooter && index == _rows.Count ? _footer : throw new ArgumentOutOfRangeException(nameof(index));
            set => throw new NotSupportedException();
        }

        /// <summary>Adds or removes only the trailing nonmusic item while retaining every music row and its scroll identity.</summary>
        /// <param name="visible">Whether the footer can be displayed for the complete current page.</param>
        public void SetFooterVisible(bool visible)
        {
            if (_showFooter == visible)
            {
                return;
            }

            _showFooter = visible;
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(
                visible ? NotifyCollectionChangedAction.Add : NotifyCollectionChangedAction.Remove, _footer, _rows.Count));
        }

        /// <inheritdoc />
        public void Dispose()
        {
            _rows.CollectionChanged -= OnRowsChanged;
        }

        /// <summary>Forwards a complete source change without enumerating any song presentations.</summary>
        /// <param name="sender">The source's lightweight row collection.</param>
        /// <param name="e">The replaced or resized row snapshot.</param>
        private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }

        /// <inheritdoc />
        public IEnumerator GetEnumerator()
        {
            foreach (var row in _rows)
            {
                yield return row;
            }

            if (_showFooter)
            {
                yield return _footer;
            }
        }

        /// <inheritdoc />
        public bool Contains(object? value) => IndexOf(value) >= 0;
        /// <inheritdoc />
        public int IndexOf(object? value) => value is SongCardRow row ? _rows.IndexOf(row)
            : _showFooter && ReferenceEquals(value, _footer) ? _rows.Count : -1;
        /// <inheritdoc />
        public void CopyTo(Array array, int index)
        {
            foreach (var row in this)
            {
                array.SetValue(row, index++);
            }
        }

        /// <inheritdoc />
        public int Add(object? value) => throw new NotSupportedException();
        /// <inheritdoc />
        public void Clear() => throw new NotSupportedException();
        /// <inheritdoc />
        public void Insert(int index, object? value) => throw new NotSupportedException();
        /// <inheritdoc />
        public void Remove(object? value) => throw new NotSupportedException();
        /// <inheritdoc />
        public void RemoveAt(int index) => throw new NotSupportedException();
    }
}
