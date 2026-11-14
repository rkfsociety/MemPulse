using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MemPulse.Core;

/// <summary>
/// Replaces a process snapshot with a single WPF-compatible collection Reset.
/// Avoids per-process CollectionView sort/filter reflows and unsafe DeferRefresh.
/// </summary>
public sealed class ProcessSnapshotCollection<T> : ObservableCollection<T>
{
    private readonly IEqualityComparer<T> _comparer;

    public ProcessSnapshotCollection() : this(null) { }

    public ProcessSnapshotCollection(IEqualityComparer<T>? comparer)
    {
        _comparer = comparer ?? EqualityComparer<T>.Default;
    }

    public void ReplaceWith(IEnumerable<T> items)
    {
        // Enumerate before clearing: consumers may hand in a lazy snapshot.
        var snapshot = items.ToArray();
        // If the collection already contains the same items in the same order, do nothing.
        if (Items.SequenceEqual(snapshot, _comparer))
            return;

        CheckReentrancy();
        Items.Clear();
        foreach (var item in snapshot)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
