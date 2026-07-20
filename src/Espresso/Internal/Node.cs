using System;
using System.Collections.Generic;
using System.Threading;

namespace Espresso.Internal;

/// <summary>
/// An entry in the cache holding the key, value, and the access/write/eviction metadata. Only the
/// key, value, and health state live on this base; every optional feature (access time, write time,
/// variable expiration, weight, queue type, deque links) is a virtual accessor returning a default
/// here and overridden by a generated subclass that carries the backing field. This keeps each
/// configuration's entry as small as the features it actually uses.
/// <para>
/// This port currently supports strong keys and strong values only. Health state is encoded by
/// swapping the key reference to a shared <see cref="RetiredSentinel"/> or <see cref="DeadSentinel"/>.
/// </para>
/// </summary>
internal abstract class Node<K, V> : IAccessOrder<Node<K, V>>, IWriteOrder<Node<K, V>>
    where K : notnull
    where V : class
{
    public const int Window = 0;
    public const int Probation = 1;
    public const int Protected = 2;

    /// <summary>Marker key reference meaning "removed from the hash table, awaiting policy removal".</summary>
    internal static readonly object RetiredSentinel = new();

    /// <summary>Marker key reference meaning "removed from the hash table and the policy".</summary>
    internal static readonly object DeadSentinel = new();

    // --- key / value / health ---

    /// <summary>The key, or a health sentinel. Stored as object so it can hold the sentinels.
    /// Accessed via <see cref="Volatile"/> so the lock-free read path observes retire/die transitions
    /// with acquire/release ordering.</summary>
    private object _keyReference;
    private V? _value; // published via Volatile

    protected Node(K key, V value)
    {
        Volatile.Write(ref _keyReference, key);
        Volatile.Write(ref _value, value);
    }

    /// <summary>The key, or default (null) if it currently holds a health sentinel.</summary>
    public K? Key
    {
        get
        {
            object k = Volatile.Read(ref _keyReference);
            return (ReferenceEquals(k, RetiredSentinel) || ReferenceEquals(k, DeadSentinel))
                ? default
                : (K)k;
        }
    }

    /// <summary>The raw reference the cache holds the entry by (the key or a health sentinel).</summary>
    public object KeyReference => Volatile.Read(ref _keyReference);

    public V? Value
    {
        get => Volatile.Read(ref _value);
        set => Volatile.Write(ref _value, value);
    }

    public bool ContainsValue(V value) => EqualityComparer<V>.Default.Equals(value, Value!);

    // --- health state ---

    public bool IsAlive
    {
        get
        {
            object k = Volatile.Read(ref _keyReference);
            return !ReferenceEquals(k, RetiredSentinel) && !ReferenceEquals(k, DeadSentinel);
        }
    }

    public bool IsRetired => ReferenceEquals(Volatile.Read(ref _keyReference), RetiredSentinel);

    public bool IsDead => ReferenceEquals(Volatile.Read(ref _keyReference), DeadSentinel);

    /// <summary>Marks the entry as removed from the hash table but still linked in the policy.</summary>
    public void Retire() => Volatile.Write(ref _keyReference, RetiredSentinel);

    /// <summary>Marks the entry as removed from both the hash table and the policy.</summary>
    public void Die()
    {
        Volatile.Write(ref _value, null);
        Volatile.Write(ref _keyReference, DeadSentinel);
    }

    // --- weight (default: unweighted) ---

    public virtual int Weight
    {
        get => 1;
        set { }
    }

    public virtual int PolicyWeight
    {
        get => 1;
        set { }
    }

    // --- variable expiration order (default: unsupported) ---

    public virtual long VariableTime
    {
        get => 0L;
        set { }
    }

    public virtual bool CasVariableTime(long expect, long update)
        => throw new NotSupportedException();

    public virtual Node<K, V>? PreviousInVariableOrder
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public virtual Node<K, V>? NextInVariableOrder
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    // --- access order (default: window, no links) ---

    public virtual int QueueType
    {
        get => Window;
        set => throw new NotSupportedException();
    }

    public bool InWindow => QueueType == Window;
    public bool InMainProbation => QueueType == Probation;
    public bool InMainProtected => QueueType == Protected;
    public void MakeWindow() => QueueType = Window;
    public void MakeMainProbation() => QueueType = Probation;
    public void MakeMainProtected() => QueueType = Protected;

    public virtual long AccessTime
    {
        get => 0L;
        set { }
    }

    public virtual Node<K, V>? GetPreviousInAccessOrder() => null;
    public virtual void SetPreviousInAccessOrder(Node<K, V>? prev) => throw new NotSupportedException();
    public virtual Node<K, V>? GetNextInAccessOrder() => null;
    public virtual void SetNextInAccessOrder(Node<K, V>? next) => throw new NotSupportedException();

    // --- write order (default: no links) ---

    public virtual long WriteTime
    {
        get => 0L;
        set { }
    }

    public virtual bool CasWriteTime(long expect, long update) => throw new NotSupportedException();

    public virtual Node<K, V>? GetPreviousInWriteOrder() => null;
    public virtual void SetPreviousInWriteOrder(Node<K, V>? prev) => throw new NotSupportedException();
    public virtual Node<K, V>? GetNextInWriteOrder() => null;
    public virtual void SetNextInWriteOrder(Node<K, V>? next) => throw new NotSupportedException();

    public override string ToString()
        => $"{GetType().Name}=[key={Key}, value={Value}, weight={Weight}, queueType={QueueType}, "
           + $"accessTimeNs={AccessTime}, writeTimeNs={WriteTime}, varTimeNs={VariableTime}]";
}
