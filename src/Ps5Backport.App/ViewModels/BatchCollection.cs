using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Ps5Backport.App.ViewModels;

/// <summary>Replaces a filtered view with one reset notification, retaining the original row objects.</summary>
internal sealed class BatchCollection<T> : ObservableCollection<T>
{
    internal void ReplaceWith(IEnumerable<T> rows)
    {
        T[] replacement = rows.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (T row in replacement) Items.Add(row);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
