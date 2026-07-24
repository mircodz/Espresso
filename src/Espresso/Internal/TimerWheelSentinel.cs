namespace Espresso.Internal;

/// <summary>
/// A sentinel node that serves as the head of a <see cref="TimerWheel{K,V}"/> bucket's circular
/// doubly-linked list. It carries only the variable-order links; it has no key, value, or health
/// state and is never a real cache entry.
/// </summary>
internal sealed class TimerWheelSentinel<K, V> : Node<K, V>
    where K : notnull
    where V : class
{
    private Node<K, V>? _prev;
    private Node<K, V>? _next;

    public TimerWheelSentinel()
        : base(default!, default!)
    {
        _prev = this;
        _next = this;
    }

    public override Node<K, V>? PreviousInVariableOrder { get => _prev; set => _prev = value; }
    public override Node<K, V>? NextInVariableOrder { get => _next; set => _next = value; }
}
