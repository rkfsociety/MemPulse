using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace MemPulse.Core;

/// <summary>
/// Replaces a process snapshot with a single WPF-compatible collection Reset.
/// Avoids per-process CollectionView sort/filter reflows and unsafe DeferRefresh.
/// </summary>
public sealed class ProcessSnapshotCollection<T> : ObservableCollection<T>
{
    public void ReplaceWith(IEnumerable<T> items)
    {
        // Enumerate before clearing: consumers may hand in a lazy snapshot.
        var snapshot = items.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in snapshot)
            Items.Add(item);

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
