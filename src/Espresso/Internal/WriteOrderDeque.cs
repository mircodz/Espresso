namespace Espresso.Internal;

/// <summary>An element that can be linked on a <see cref="WriteOrderDeque{E}"/>.</summary>
internal interface IWriteOrder<T> where T : class, IWriteOrder<T>
{
    T? GetPreviousInWriteOrder();
    void SetPreviousInWriteOrder(T? prev);
    T? GetNextInWriteOrder();
    void SetNextInWriteOrder(T? next);
}

/// <summary>A linked deque representing a write-order queue.</summary>
internal sealed class WriteOrderDeque<E> : AbstractLinkedDeque<E> where E : class, IWriteOrder<E>
{
    public override E? GetPrevious(E e) => e.GetPreviousInWriteOrder();
    public override void SetPrevious(E e, E? prev) => e.SetPreviousInWriteOrder(prev);
    public override E? GetNext(E e) => e.GetNextInWriteOrder();
    public override void SetNext(E e, E? next) => e.SetNextInWriteOrder(next);

    /// <summary>Fast-path containment: an element is present iff it is linked or is the head.</summary>
    public override bool Contains(E e)
        => e.GetPreviousInWriteOrder() != null
           || e.GetNextInWriteOrder() != null
           || ReferenceEquals(e, First);

    public override bool Remove(E e)
    {
        if (Contains(e))
        {
            Unlink(e);
            return true;
        }
        return false;
    }
}
