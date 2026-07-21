using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Espresso.Internal;

/// <summary>
/// A concurrent hash table supporting full concurrency of retrievals and high expected concurrency
/// for updates. The distinguishing property versus <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/>
/// is that the <b>update/compute functions run under the bin lock, atomically, and at most once</b>
/// per successful call — the linearizability the cache relies on.
/// <para>
/// Bins are singly-linked <see cref="Node"/> chains; each bin is locked by monitoring its head node,
/// and inserts into an empty bin use a lock-free compare-and-set on the table slot. Write concurrency
/// therefore scales with the number of bins (the table grows as it fills) rather than a fixed
/// striping level. During a resize a migrated bin's head is replaced by a <see cref="ForwardingNode"/>:
/// a reader follows it to the new table, while a writer waits for the resize to finish and retries
/// against the published table, so no update is lost. Keys and values
/// stay strongly typed end-to-end with <b>no boxing</b>; values are reference types so a <c>null</c>
/// result means "remove".
/// </para>
/// </summary>
internal sealed class ConcurrentHashMap<TKey, TValue>
    where TKey : notnull
    where TValue : class
{
    private const int DefaultCapacity = 16;
    private const int MaximumCapacity = 1 << 30;
    private const int HashBits = 0x7fffffff;

    /// <summary>A key/value entry in a bin chain.</summary>
    internal class Node
    {
        internal readonly int Hash;
        internal readonly TKey Key;
        internal TValue Value;   // published via Volatile.Read/Write
        internal Node? Next;     // published via Volatile.Read/Write

        internal Node(int hash, TKey key, TValue value, Node? next)
        {
            Hash = hash;
            Key = key;
            Value = value;
            Next = next;
        }
    }

    /// <summary>
    /// Placed at a bin head once it has been migrated during a resize. Its presence tells readers and
    /// writers to continue on <see cref="NextTable"/>.
    /// </summary>
    private sealed class ForwardingNode : Node
    {
        internal readonly Node?[] NextTable;
        internal ForwardingNode(Node?[] nextTable) : base(0, default!, default!, null) => NextTable = nextTable;
    }

    private readonly IEqualityComparer<TKey>? _comparer; // null => use the devirtualizable default
    private readonly object _resizeLock = new();

    private volatile Node?[] _table;
    private int _threshold;

    // LongAdder-style striped size counter. Cells are strided by CounterCellStride longs so each
    // active counter owns a 128-byte cache sector (avoids false sharing between adjacent counters).
    private const int CounterCellStride = 16;
    private long _baseCount;
    private long[]? _counterCells;
    private int _cellsBusy;

    /// <summary>Returns this thread's slot (a strided index) into the counter-cell array.</summary>
    private static int CounterSlot(long[] cells)
    {
        int logicalCells = cells.Length / CounterCellStride;
        return (Environment.CurrentManagedThreadId & (logicalCells - 1)) * CounterCellStride;
    }

    /// <summary>
    /// Creates the map. <paramref name="initialCapacity"/> presizes the bin array (fewer resizes).
    /// <paramref name="concurrencyLevel"/> is an optional lower bound on the initial bin count;
    /// since locking is per-bin it only nudges the initial
    /// size and imposes no fixed cap on concurrent writers.
    /// </summary>
    public ConcurrentHashMap(
        int initialCapacity = DefaultCapacity,
        int concurrencyLevel = 1,
        IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(concurrencyLevel);
        // Storing null for the default comparer lets the JIT devirtualize/inline equality for value
        // types on the hot path (the Dictionary<TKey,TValue> pattern), avoiding interface dispatch.
        _comparer = ReferenceEquals(comparer, EqualityComparer<TKey>.Default) ? null : comparer;
        int cap = TableSizeFor(Math.Max(initialCapacity, concurrencyLevel));
        _table = new Node?[cap];
        _threshold = cap - (cap >>> 2); // 0.75 load factor
    }

    /// <summary>Hashes a key without boxing; uses the intrinsifiable default path when possible.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int HashOf(TKey key)
        => Spread(_comparer == null ? key.GetHashCode() : _comparer.GetHashCode(key));

    /// <summary>Key equality; the default path is JIT-inlined for value types.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool KeyEquals(TKey a, TKey b)
        => _comparer == null ? EqualityComparer<TKey>.Default.Equals(a, b) : _comparer.Equals(a, b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Spread(int h) => (h ^ (h >>> 16)) & HashBits;

    private static int TableSizeFor(int c)
    {
        if (c <= DefaultCapacity)
        {
            return DefaultCapacity;
        }
        int n = Common.CeilingPowerOfTwo(c);
        return n >= MaximumCapacity ? MaximumCapacity : n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Node? TabAt(Node?[] tab, int i) => Volatile.Read(ref tab[i]);

    /// <summary>
    /// Called by a writer that encountered a <see cref="ForwardingNode"/>. A writer must not mutate
    /// the resize's half-built next table (that would race the resizer's unsynchronized bin
    /// construction and lose the write); instead it waits for the resize to finish — the resizer
    /// holds <see cref="_resizeLock"/> for the whole operation and publishes <c>_table</c> last — and
    /// then returns the now-published table to retry against. Readers, by contrast, may safely follow
    /// the forward pointer because a bin is fully migrated before it is marked as forwarded.
    /// </summary>
    private Node?[] AwaitResize()
    {
        lock (_resizeLock)
        {
            return _table; // resize complete; _table now points at the finished new table
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SetTabAt(Node?[] tab, int i, Node? v) => Volatile.Write(ref tab[i], v);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool CasTabAt(Node?[] tab, int i, Node? expect, Node? update)
        => Interlocked.CompareExchange(ref tab[i], update, expect) == expect;

    public long Count
    {
        get
        {
            long sum = SumCount();
            return sum < 0 ? 0 : sum;
        }
    }

    /// <summary>Sums the base counter and any striped cells. Cheap when uncontended (cells == null).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private long SumCount()
    {
        long sum = Interlocked.Read(ref _baseCount);
        long[]? cells = _counterCells;
        if (cells != null)
        {
            // Only the strided slots hold counters; the padding longs between them are always zero.
            for (int i = 0; i < cells.Length; i += CounterCellStride)
            {
                sum += Volatile.Read(ref cells[i]);
            }
        }
        return sum;
    }

    public bool IsEmpty => Count == 0;

    /// <summary>Returns the value for the key, or <c>null</c> if absent.</summary>
    public TValue? GetOrDefault(TKey key)
    {
        int h = HashOf(key);
        Node?[] tab = _table;
        while (true)
        {
            int n = tab.Length;
            Node? e = TabAt(tab, (n - 1) & h);
            if (e is ForwardingNode fwd)
            {
                tab = fwd.NextTable;
                continue;
            }
            while (e != null)
            {
                if (e.Hash == h && KeyEquals(e.Key, key))
                {
                    return Volatile.Read(ref e.Value);
                }
                e = Volatile.Read(ref e.Next);
            }
            return null;
        }
    }

    public bool TryGetValue(TKey key, out TValue value)
    {
        TValue? v = GetOrDefault(key);
        value = v!;
        return v != null;
    }

    public bool ContainsKey(TKey key) => GetOrDefault(key) != null;

    public TValue? Put(TKey key, TValue value) => PutVal(key, value, onlyIfAbsent: false);

    public TValue? PutIfAbsent(TKey key, TValue value) => PutVal(key, value, onlyIfAbsent: true);

    private TValue? PutVal(TKey key, TValue value, bool onlyIfAbsent)
    {
        int h = HashOf(key);
        Node?[] tab = _table;
        while (true)
        {
            int n = tab.Length;
            int i = (n - 1) & h;
            Node? f = TabAt(tab, i);
            if (f == null)
            {
                if (CasTabAt(tab, i, null, new Node(h, key, value, null)))
                {
                    AddCount(1);
                    return null;
                }
                continue; // lost the empty-bin race, retry
            }
            if (f is ForwardingNode fwd)
            {
                tab = AwaitResize();
                continue;
            }

            TValue? oldVal = null;
            bool inserted = false;
            lock (f)
            {
                if (TabAt(tab, i) != f)
                {
                    tab = _table;
                    continue;
                }
                Node e = f;
                while (true)
                {
                    if (e.Hash == h && KeyEquals(e.Key, key))
                    {
                        oldVal = e.Value;
                        if (!onlyIfAbsent)
                        {
                            Volatile.Write(ref e.Value, value);
                        }
                        break;
                    }
                    Node? next = e.Next;
                    if (next == null)
                    {
                        Volatile.Write(ref e.Next, new Node(h, key, value, null));
                        inserted = true;
                        break;
                    }
                    e = next;
                }
            }

            if (inserted)
            {
                AddCount(1);
            }
            return oldVal;
        }
    }

    public TValue? Remove(TKey key) => ReplaceNode(key, null, null);

    public bool Remove(TKey key, TValue value) => ReplaceNode(key, null, value) != null;

    public TValue? Replace(TKey key, TValue value) => ReplaceNode(key, value, null);

    public bool Replace(TKey key, TValue oldValue, TValue newValue)
        => ReplaceNode(key, newValue, oldValue) != null;

    private TValue? ReplaceNode(TKey key, TValue? value, TValue? expected)
    {
        int h = HashOf(key);
        Node?[] tab = _table;
        while (true)
        {
            int n = tab.Length;
            int i = (n - 1) & h;
            Node? f = TabAt(tab, i);
            if (f == null)
            {
                return null;
            }
            if (f is ForwardingNode fwd)
            {
                tab = AwaitResize();
                continue;
            }

            TValue? oldVal = null;
            bool removed = false;
            lock (f)
            {
                if (TabAt(tab, i) != f)
                {
                    tab = _table;
                    continue;
                }
                Node? pred = null;
                Node e = f;
                while (true)
                {
                    if (e.Hash == h && KeyEquals(e.Key, key))
                    {
                        TValue ev = e.Value;
                        if (expected == null || ReferenceEquals(expected, ev) || expected.Equals(ev))
                        {
                            oldVal = ev;
                            if (value != null)
                            {
                                Volatile.Write(ref e.Value, value);
                            }
                            else if (pred != null)
                            {
                                Volatile.Write(ref pred.Next, e.Next);
                                removed = true;
                            }
                            else
                            {
                                SetTabAt(tab, i, e.Next);
                                removed = true;
                            }
                        }
                        break;
                    }
                    pred = e;
                    Node? next = e.Next;
                    if (next == null)
                    {
                        break;
                    }
                    e = next;
                }
            }

            if (removed)
            {
                AddCount(-1);
            }
            return oldVal;
        }
    }

    public TValue? ComputeIfAbsent(TKey key, Func<TKey, TValue?> mappingFunction)
    {
        int h = HashOf(key);
        Node?[] tab = _table;
        while (true)
        {
            int n = tab.Length;
            int i = (n - 1) & h;
            Node? f = TabAt(tab, i);
            if (f == null)
            {
                var node = new Node(h, key, null!, null);
                TValue? val;
                lock (node)
                {
                    if (!CasTabAt(tab, i, null, node))
                    {
                        continue; // lost the race, retry
                    }
                    bool added = false;
                    try
                    {
                        val = mappingFunction(key);
                        if (val != null)
                        {
                            node.Value = val;
                            added = true;
                        }
                    }
                    finally
                    {
                        if (!added)
                        {
                            SetTabAt(tab, i, null);
                        }
                    }
                }
                if (val != null)
                {
                    AddCount(1);
                }
                return val;
            }
            if (f is ForwardingNode fwd)
            {
                tab = AwaitResize();
                continue;
            }

            // Lock-free fast path: if the key is already present with a published value, return it
            // without locking. A node whose value is still null is an in-flight placeholder (another
            // thread is computing under its lock); fall through to the lock and wait for it.
            for (Node? e = f; e != null; e = Volatile.Read(ref e.Next))
            {
                if (e.Hash == h && KeyEquals(e.Key, key))
                {
                    TValue? existing = Volatile.Read(ref e.Value);
                    if (existing != null)
                    {
                        return existing;
                    }
                    break; // placeholder in progress — take the lock below
                }
            }

            TValue? result = null;
            bool inserted = false;
            bool present = false;
            lock (f)
            {
                if (TabAt(tab, i) != f)
                {
                    tab = _table;
                    continue;
                }
                Node e = f;
                while (true)
                {
                    if (e.Hash == h && KeyEquals(e.Key, key))
                    {
                        result = e.Value;
                        present = true;
                        break;
                    }
                    Node? next = e.Next;
                    if (next == null)
                    {
                        result = mappingFunction(key);
                        if (result != null)
                        {
                            Volatile.Write(ref e.Next, new Node(h, key, result, null));
                            inserted = true;
                        }
                        break;
                    }
                    e = next;
                }
            }

            if (present)
            {
                return result;
            }
            if (inserted)
            {
                AddCount(1);
            }
            return result;
        }
    }

    public TValue? ComputeIfPresent(TKey key, Func<TKey, TValue, TValue?> remappingFunction)
        => ComputeInternal(key, remappingFunction, onlyIfPresent: true);

    public TValue? Compute(TKey key, Func<TKey, TValue?, TValue?> remappingFunction)
        => ComputeInternal(key, remappingFunction, onlyIfPresent: false);

    private TValue? ComputeInternal(TKey key, Delegate remapping, bool onlyIfPresent)
    {
        int h = HashOf(key);
        Node?[] tab = _table;
        while (true)
        {
            int n = tab.Length;
            int i = (n - 1) & h;
            Node? f = TabAt(tab, i);
            if (f == null)
            {
                if (onlyIfPresent)
                {
                    return null;
                }
                var node = new Node(h, key, null!, null);
                TValue? computed;
                lock (node)
                {
                    if (!CasTabAt(tab, i, null, node))
                    {
                        continue;
                    }
                    bool added = false;
                    try
                    {
                        computed = ((Func<TKey, TValue?, TValue?>)remapping)(key, null);
                        if (computed != null)
                        {
                            node.Value = computed;
                            added = true;
                        }
                    }
                    finally
                    {
                        if (!added)
                        {
                            SetTabAt(tab, i, null);
                        }
                    }
                }
                if (computed != null)
                {
                    AddCount(1);
                }
                return computed;
            }
            if (f is ForwardingNode fwd)
            {
                tab = AwaitResize();
                continue;
            }

            TValue? val = null;
            int delta = 0;
            lock (f)
            {
                if (TabAt(tab, i) != f)
                {
                    tab = _table;
                    continue;
                }
                Node? pred = null;
                Node e = f;
                while (true)
                {
                    if (e.Hash == h && KeyEquals(e.Key, key))
                    {
                        val = onlyIfPresent
                            ? ((Func<TKey, TValue, TValue?>)remapping)(key, e.Value)
                            : ((Func<TKey, TValue?, TValue?>)remapping)(key, e.Value);
                        if (val != null)
                        {
                            Volatile.Write(ref e.Value, val);
                        }
                        else
                        {
                            delta = -1;
                            if (pred != null)
                            {
                                Volatile.Write(ref pred.Next, e.Next);
                            }
                            else
                            {
                                SetTabAt(tab, i, e.Next);
                            }
                        }
                        break;
                    }
                    pred = e;
                    Node? next = e.Next;
                    if (next == null)
                    {
                        if (!onlyIfPresent)
                        {
                            val = ((Func<TKey, TValue?, TValue?>)remapping)(key, null);
                            if (val != null)
                            {
                                delta = 1;
                                Volatile.Write(ref e.Next, new Node(h, key, val, null));
                            }
                        }
                        break;
                    }
                    e = next;
                }
            }

            if (delta != 0)
            {
                AddCount(delta);
            }
            return val;
        }
    }

    public TValue? Merge(TKey key, TValue value, Func<TValue, TValue, TValue?> remappingFunction)
        => Compute(key, (_, existing) => existing == null ? value : remappingFunction(existing, value));

    public void Clear()
    {
        long delta = 0;
        Node?[] tab = _table;
        for (int i = 0; i < tab.Length;)
        {
            Node? f = TabAt(tab, i);
            if (f == null)
            {
                i++;
                continue;
            }
            if (f is ForwardingNode fwd)
            {
                tab = AwaitResize();
                i = 0;
                continue;
            }
            lock (f)
            {
                if (TabAt(tab, i) == f)
                {
                    for (Node? p = f; p != null; p = p.Next)
                    {
                        delta--;
                    }
                    SetTabAt(tab, i, null);
                    i++;
                }
            }
        }
        if (delta != 0)
        {
            AddCount(delta);
        }
    }

    /// <summary>
    /// Weakly-consistent enumeration over live entries. On
    /// encountering a <see cref="ForwardingNode"/> (a bin already migrated by a concurrent resize) it
    /// descends into the new table and saves the current position on a small stack, so every entry is
    /// visited at most once. (The previous implementation restarted from bin 0 of the doubled table,
    /// re-yielding every already-visited entry.)
    /// </summary>
    public IEnumerator<KeyValuePair<TKey, TValue>> GetEnumerator()
    {
        Node?[]? tab = _table;
        int baseSize = tab.Length;
        int baseIndex = 0;
        int baseLimit = tab.Length;
        int index = 0;
        TableStack? stack = null;
        TableStack? spare = null;
        Node? e = null;

        while (true)
        {
            // Walk the current bin's chain, emitting live entries.
            if (e != null)
            {
                Node cur = e;
                e = Volatile.Read(ref cur.Next);
                TValue v = Volatile.Read(ref cur.Value);
                if (v != null)
                {
                    yield return new KeyValuePair<TKey, TValue>(cur.Key, v);
                }
                continue;
            }

            // Advance to the next bin.
            Node?[]? t = tab;
            int i = index;
            int n;
            if (baseIndex >= baseLimit || t == null || (n = t.Length) <= i || i < 0)
            {
                yield break;
            }
            e = TabAt(t, i);
            if (e is ForwardingNode fwd)
            {
                // Descend into the migrated table, saving where we were (pushState).
                tab = fwd.NextTable;
                e = null;
                TableStack s = spare ?? new TableStack();
                if (spare != null) spare = spare.Next;
                s.Tab = t;
                s.Length = n;
                s.Index = i;
                s.Next = stack;
                stack = s;
                continue;
            }

            // Compute the next bin index (recoverState pops saved tables as their ranges are done).
            if (stack != null)
            {
                TableStack? s;
                while ((s = stack) != null && (index += s.Length) >= n)
                {
                    n = s.Length;
                    index = s.Index;
                    tab = s.Tab;
                    s.Tab = null;
                    TableStack? nx = s.Next;
                    s.Next = spare;
                    stack = nx;
                    spare = s;
                }
                if (s == null && (index += baseSize) >= n)
                {
                    index = ++baseIndex;
                }
            }
            else if ((index = i + baseSize) >= n)
            {
                index = ++baseIndex;
            }
        }
    }

    /// <summary>Saved traverser position for a table left behind when descending through a resize.</summary>
    private sealed class TableStack
    {
        internal int Length;
        internal int Index;
        internal Node?[]? Tab;
        internal TableStack? Next;
    }

    // ----- size counter + resize -----

    private void AddCount(long x)
    {
        long[]? cells = _counterCells;
        if (cells != null)
        {
            Interlocked.Add(ref cells[CounterSlot(cells)], x);
        }
        else
        {
            long b = Interlocked.Read(ref _baseCount);
            if (Interlocked.CompareExchange(ref _baseCount, b + x, b) != b)
            {
                InflateCounterAndAdd(x);
            }
            else if (x > 0 && b + x >= Volatile.Read(ref _threshold))
            {
                // Uncontended common case: threshold check needs no cell scan.
                TryResize(b + x);
            }
            return;
        }

        if (x > 0)
        {
            long s = SumCount();
            if (s >= Volatile.Read(ref _threshold))
            {
                TryResize(s);
            }
        }
    }

    private void InflateCounterAndAdd(long x)
    {
        if (Interlocked.CompareExchange(ref _cellsBusy, 1, 0) == 0)
        {
            try
            {
                if (_counterCells == null)
                {
                    int logicalCells = Math.Max(2, Common.CeilingPowerOfTwo(Environment.ProcessorCount));
                    // Allocate CounterCellStride longs per logical counter so each lands on its own
                    // cache line (padding slots between counters stay zero).
                    _counterCells = new long[logicalCells * CounterCellStride];
                }
            }
            finally
            {
                Volatile.Write(ref _cellsBusy, 0);
            }
        }
        long[]? c = _counterCells;
        if (c != null)
        {
            Interlocked.Add(ref c[CounterSlot(c)], x);
        }
        else
        {
            Interlocked.Add(ref _baseCount, x);
        }
    }

    private void TryResize(long knownSize)
    {
        lock (_resizeLock)
        {
            Node?[] oldTab = _table;
            int oldCap = oldTab.Length;
            // Re-check under the lock using the caller's already-computed size to avoid a second scan.
            if (knownSize < _threshold || oldCap >= MaximumCapacity)
            {
                return;
            }
            int newCap = oldCap << 1;
            var newTab = new Node?[newCap];
            var forward = new ForwardingNode(newTab);

            for (int i = 0; i < oldCap; i++)
            {
                while (true)
                {
                    Node? f = TabAt(oldTab, i);
                    if (f == null)
                    {
                        // Claim the empty bin so a late writer sees the forward marker.
                        if (CasTabAt(oldTab, i, null, forward))
                        {
                            break;
                        }
                        continue;
                    }
                    lock (f)
                    {
                        if (TabAt(oldTab, i) != f)
                        {
                            continue;
                        }
                        for (Node? e = f; e != null; e = e.Next)
                        {
                            int j = (newCap - 1) & e.Hash;
                            newTab[j] = new Node(e.Hash, e.Key, Volatile.Read(ref e.Value), newTab[j]);
                        }
                        SetTabAt(oldTab, i, forward);
                    }
                    break;
                }
            }

            _table = newTab;
            Volatile.Write(ref _threshold, newCap - (newCap >>> 2));
        }
    }
}
