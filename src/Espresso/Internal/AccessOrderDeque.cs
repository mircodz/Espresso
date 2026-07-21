namespace Espresso.Internal;

/// <summary>An element that can be linked on an <see cref="AccessOrderDeque{E}"/>.</summary>
internal interface IAccessOrder<T> where T : class, IAccessOrder<T>
{
    T? GetPreviousInAccessOrder();
    void SetPreviousInAccessOrder(T? prev);
    T? GetNextInAccessOrder();
    void SetNextInAccessOrder(T? next);
}

/// <summary>A linked deque representing an access-order queue.</summary>
internal sealed class AccessOrderDeque<E> : AbstractLinkedDeque<E> where E : class, IAccessOrder<E>
{
    public override E? GetPrevious(E e) => e.GetPreviousInAccessOrder();
    public override void SetPrevious(E e, E? prev) => e.SetPreviousInAccessOrder(prev);
    public override E? GetNext(E e) => e.GetNextInAccessOrder();
    public override void SetNext(E e, E? next) => e.SetNextInAccessOrder(next);

    /// <summary>Fast-path containment: an element is present iff it is linked or is the head.</summary>
    public override bool Contains(E e)
        => e.GetPreviousInAccessOrder() != null
           || e.GetNextInAccessOrder() != null
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
