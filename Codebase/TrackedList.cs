using System;
using System.Collections.ObjectModel;

namespace Codebase;

// Owned by the game thread. Change callbacks also attach/detach item-level tracking.
internal sealed class TrackedList<T> : Collection<T>
{
    private readonly Action<T, bool> _changed;

    internal TrackedList(Action<T, bool> changed) { _changed = changed; }

    protected override void InsertItem(int index, T item)
    {
        base.InsertItem(index, item);
        _changed(item, true);
    }

    protected override void RemoveItem(int index)
    {
        T item = this[index];
        base.RemoveItem(index);
        _changed(item, false);
    }

    protected override void SetItem(int index, T item)
    {
        T previous = this[index];
        base.SetItem(index, item);
        _changed(previous, false);
        _changed(item, true);
    }

    protected override void ClearItems()
    {
        T[] previous = new T[Count];
        CopyTo(previous, 0);
        base.ClearItems();
        foreach (T item in previous) _changed(item, false);
    }

    internal int RemoveAll(Predicate<T> predicate)
    {
        int removed = 0;
        for (int i = Count - 1; i >= 0; i--)
        {
            if (!predicate(this[i])) continue;
            RemoveAt(i);
            removed++;
        }
        return removed;
    }
}
