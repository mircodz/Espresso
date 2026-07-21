using System;
using System.Collections;
using System.Collections.Generic;

namespace Espresso.Internal;

/// <summary>
/// A doubly-linked deque whose link pointers are stored directly on the element. It has no capacity
/// restriction and is <b>not</b> thread-safe (in the cache it is guarded by the eviction lock).
/// Null elements are prohibited.
/// <para>
/// Most operations run in constant time by assuming the element argument already belongs to this
/// deque; violating that assumption yields non-deterministic behavior. An element can exist in only
/// one deque of a given kind at a time. Enumerators are fail-fast: structural modification during
/// iteration (other than via the enumerator itself) throws <see cref="InvalidOperationException"/>.
/// </para>
/// </summary>
internal interface ILinkedDeque<E> : IEnumerable<E> where E : class
{
    int Count { get; }
    bool IsEmpty { get; }

    E? PeekFirst { get; }
    E? PeekLast { get; }

    bool IsFirst(E? e);
    bool IsLast(E? e);

    void MoveToFront(E e);
    void MoveToBack(E e);

    bool Offer(E e);
    bool OfferFirst(E e);
    bool OfferLast(E e);

    E? Poll();
    E? PollFirst();
    E? PollLast();

    bool Remove(E e);
    bool Contains(E e);
    void Clear();

    E? GetPrevious(E e);
    void SetPrevious(E e, E? prev);
    E? GetNext(E e);
    void SetNext(E e, E? next);

    /// <summary>Enumerates from last to first.</summary>
    IEnumerator<E> GetDescendingEnumerator();
}

/// <summary>
/// Skeletal implementation of <see cref="ILinkedDeque{E}"/>. Concrete subclasses only define how the
/// prev/next links are read and written on the element.
/// </summary>
internal abstract class AbstractLinkedDeque<E> : ILinkedDeque<E> where E : class
{
    // The first/last elements are manipulated directly (rather than via a sentinel) to avoid null
    // checks in the hot paths. Links on a removed element are cleared to aid the GC.

    protected E? First;
    protected E? Last;
    protected int ModCount;

    public abstract E? GetPrevious(E e);
    public abstract void SetPrevious(E e, E? prev);
    public abstract E? GetNext(E e);
    public abstract void SetNext(E e, E? next);
    public abstract bool Contains(E e);

    private void LinkFirst(E e)
    {
        E? f = First;
        First = e;
        if (f == null)
        {
            Last = e;
        }
        else
        {
            SetPrevious(f, e);
            SetNext(e, f);
        }
        ModCount++;
    }

    private void LinkLast(E e)
    {
        E? l = Last;
        Last = e;
        if (l == null)
        {
            First = e;
        }
        else
        {
            SetNext(l, e);
            SetPrevious(e, l);
        }
        ModCount++;
    }

    private E UnlinkFirst()
    {
        E f = First!;
        E? next = GetNext(f);
        SetNext(f, null);
        First = next;
        if (next == null)
        {
            Last = null;
        }
        else
        {
            SetPrevious(next, null);
        }
        ModCount++;
        return f;
    }

    private E UnlinkLast()
    {
        E l = Last!;
        E? prev = GetPrevious(l);
        SetPrevious(l, null);
        Last = prev;
        if (prev == null)
        {
            First = null;
        }
        else
        {
            SetNext(prev, null);
        }
        ModCount++;
        return l;
    }

    /// <summary>Unlinks a non-null element known to belong to this deque.</summary>
    protected void Unlink(E e)
    {
        E? prev = GetPrevious(e);
        E? next = GetNext(e);

        if (prev == null)
        {
            First = next;
        }
        else
        {
            SetNext(prev, next);
            SetPrevious(e, null);
        }

        if (next == null)
        {
            Last = prev;
        }
        else
        {
            SetPrevious(next, prev);
            SetNext(e, null);
        }
        ModCount++;
    }

    public bool IsEmpty => First == null;

    public E? PeekFirst => First;
    public E? PeekLast => Last;

    /// <summary>Not a constant-time operation; walks the chain.</summary>
    public int Count
    {
        get
        {
            int size = 0;
            for (E? e = First; e != null; e = GetNext(e))
            {
                size++;
            }
            return size;
        }
    }

    public void Clear()
    {
        E? e = First;
        while (e != null)
        {
            E? next = GetNext(e);
            SetPrevious(e, null);
            SetNext(e, null);
            e = next;
        }
        First = Last = null;
        ModCount++;
    }

    public bool IsFirst(E? e) => e != null && ReferenceEquals(e, First);

    public bool IsLast(E? e) => e != null && ReferenceEquals(e, Last);

    public void MoveToFront(E e)
    {
        if (!ReferenceEquals(e, First))
        {
            Unlink(e);
            LinkFirst(e);
        }
    }

    public void MoveToBack(E e)
    {
        if (!ReferenceEquals(e, Last))
        {
            Unlink(e);
            LinkLast(e);
        }
    }

    public bool Offer(E e) => OfferLast(e);

    public bool OfferFirst(E e)
    {
        if (e == null)
        {
            throw new ArgumentNullException(nameof(e));
        }
        if (Contains(e))
        {
            return false;
        }
        LinkFirst(e);
        return true;
    }

    public bool OfferLast(E e)
    {
        if (e == null)
        {
            throw new ArgumentNullException(nameof(e));
        }
        if (Contains(e))
        {
            return false;
        }
        LinkLast(e);
        return true;
    }

    public E? Poll() => PollFirst();

    public E? PollFirst() => IsEmpty ? null : UnlinkFirst();

    public E? PollLast() => IsEmpty ? null : UnlinkLast();

    public abstract bool Remove(E e);

    public IEnumerator<E> GetEnumerator() => new LinkedEnumerator(this, First, ascending: true);

    public IEnumerator<E> GetDescendingEnumerator() => new LinkedEnumerator(this, Last, ascending: false);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private sealed class LinkedEnumerator : IEnumerator<E>
    {
        private readonly AbstractLinkedDeque<E> _deque;
        private readonly bool _ascending;
        private int _expectedModCount;
        private E? _cursor;
        private E? _current;

        internal LinkedEnumerator(AbstractLinkedDeque<E> deque, E? start, bool ascending)
        {
            _deque = deque;
            _ascending = ascending;
            _expectedModCount = deque.ModCount;
            _cursor = start;
        }

        public E Current => _current!;
        object IEnumerator.Current => _current!;

        public bool MoveNext()
        {
            CheckForConcurrentModification();
            if (_cursor == null)
            {
                _current = null;
                return false;
            }
            _current = _cursor;
            _cursor = _ascending ? _deque.GetNext(_cursor) : _deque.GetPrevious(_cursor);
            return true;
        }

        public void Reset() => throw new NotSupportedException();

        public void Dispose() { }

        private void CheckForConcurrentModification()
        {
            if (_deque.ModCount != _expectedModCount)
            {
                throw new InvalidOperationException("deque was modified during enumeration");
            }
        }
    }
}
