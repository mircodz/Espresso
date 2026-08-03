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

    private volatile Node?[] _table;

    // Faithful port of JDK ConcurrentHashMap's cooperative resize protocol.
    //
    // _sizeCtl encodes both the grow threshold and the resize state:
    //   * positive => the element-count threshold at which the table grows (0.75 * capacity);
    //   * negative => a resize is in progress. The high 16 bits are the resize stamp for the
    //     current oldCap (identifies the generation); the low bits count active resizers + 1.
    //     The initiator installs (rs << RESIZE_STAMP_SHIFT) + 2; each helper adds 1; each worker
    //     that runs out of bins to claim subtracts 1; the worker that decrements it back to the
    //     "+2" base is the last one and performs the final sweep + publish.
    private int _sizeCtl;

    // The staging table being built during a resize (null except mid-resize). Published as _table
    // by the last finisher, then cleared.
    private volatile Node?[]? _nextTable;

    // Next bin index a worker may claim, counting DOWN from oldCap toward 0. Workers claim a stride
    // via CAS; <= 0 means all bins have been handed out.
    private int _transferIndex;

    private const int MinTransferStride = 16;
    private const int ResizeStampBits = 16;
    private const int ResizeStampShift = 32 - ResizeStampBits;
    private const int MaxResizers = (1 << (32 - ResizeStampBits)) - 1;
    private static readonly int NCpu = Environment.ProcessorCount;

    /// <summary>
    /// The stamp identifying a resize of a table of size <paramref name="n"/>. The top bit is set so
    /// that <c>stamp &lt;&lt; RESIZE_STAMP_SHIFT</c> is negative (marks _sizeCtl as "resizing").
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ResizeStamp(int n)
        => System.Numerics.BitOperations.LeadingZeroCount((uint)n) | (1 << (ResizeStampBits - 1));

    // Striped size counter: threads add into per-thread cells to avoid a single hot counter. Cells are
    // strided by CounterCellStride longs so each active counter owns a 128-byte cache sector.
    private const int CounterCellStride = 16;
    private long _baseCount;
    private volatile long[]? _counterCells;
    private int _cellsBusy;

    // Per-thread hash into the counter cells. Seeded lazily; advanced (xorshift) on CAS contention so
    // colliding threads migrate to different cells.
    [ThreadStatic] private static int _cellProbe;

    private static int AdvanceProbe(int probe)
    {
        probe ^= probe << 13;
        probe ^= probe >>> 17;
        probe ^= probe << 5;
        return probe;
    }

    private static int LogicalCell(long[] cells, int probe) => (probe & (cells.Length / CounterCellStride - 1)) * CounterCellStride;

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
        _sizeCtl = cap - (cap >>> 2); // 0.75 load factor threshold
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
    /// Called by a writer that encountered a <see cref="ForwardingNode"/> <paramref name="f"/>. The
    /// writer joins the in-progress transfer (helping migrate bins) and returns <c>f.NextTable</c> for
    /// the caller to retry against — the writer's target bin is fully migrated before the forward is
    /// installed, so it can make progress on the new table without waiting for the whole resize.
    /// Mirrors JDK's <c>helpTransfer</c>: it registers as a resizer in <see cref="_sizeCtl"/> and runs
    /// <see cref="Transfer"/>. Callers re-read the volatile <see cref="_table"/> at the top of their
    /// loop, so a stale table is never used across a help.
    /// </summary>
    private Node?[] HelpTransfer(Node?[] tab, ForwardingNode f)
    {
        Node?[] nextTab = f.NextTable;
        int rs = ResizeStamp(tab.Length);
        while (nextTab == _nextTable && _table == tab)
        {
            int sc = Volatile.Read(ref _sizeCtl);
            if (sc >= 0)
            {
                break; // resize finished
            }
            // Stop helping if the stamp no longer matches this generation, the resizer count is
            // saturated/drained, or all bins have been claimed.
            if ((sc >>> ResizeStampShift) != rs
                || sc == (rs << ResizeStampShift) + 1
                || sc == (rs << ResizeStampShift) + MaxResizers
                || Volatile.Read(ref _transferIndex) <= 0)
            {
                break;
            }
            if (Interlocked.CompareExchange(ref _sizeCtl, sc + 1, sc) == sc)
            {
                Transfer(tab, nextTab);
                break;
            }
        }
        return nextTab;
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
        // A null value would increment Count for an entry that GetOrDefault treats as absent,
        // permanently desyncing the size counter.
        ArgumentNullException.ThrowIfNull(value);
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
                    AddCount(1, 0);
                    return null;
                }
                continue; // lost the empty-bin race, retry
            }
            if (f is ForwardingNode fwd)
            {
                tab = HelpTransfer(tab, fwd);
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
                AddCount(1, 2);
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
                tab = HelpTransfer(tab, fwd);
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
                AddCount(-1, -1);
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
                            Volatile.Write(ref node.Value, val);
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
                    AddCount(1, 2);
                }
                return val;
            }
            if (f is ForwardingNode fwd)
            {
                tab = HelpTransfer(tab, fwd);
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
                AddCount(1, 2);
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
                            Volatile.Write(ref node.Value, computed);
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
                    AddCount(1, 2);
                }
                return computed;
            }
            if (f is ForwardingNode fwd)
            {
                tab = HelpTransfer(tab, fwd);
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
                AddCount(delta, -1);
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
                tab = HelpTransfer(tab, fwd);
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
            AddCount(delta, -1);
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

    // check is the length of the bin the caller just touched: it gates the resize check so a short
    // bin (<= 1, the common case) skips the SumCount scan entirely. A negative check (deletes) never
    // triggers a resize.
    private void AddCount(long x, int check)
    {
        long[]? cells = _counterCells;
        long s;
        if (cells != null)
        {
            int probe = _cellProbe;
            ref long cell = ref cells[LogicalCell(cells, probe)];
            long v = Interlocked.Read(ref cell);
            if (Interlocked.CompareExchange(ref cell, v + x, v) != v)
            {
                FullAddCount(x);
            }
            if (check <= 1)
            {
                return;
            }
            s = SumCount();
        }
        else
        {
            long b = Interlocked.Read(ref _baseCount);
            if (Interlocked.CompareExchange(ref _baseCount, b + x, b) != b)
            {
                FullAddCount(x);
                if (check <= 1)
                {
                    return;
                }
                s = SumCount();
            }
            else
            {
                s = b + x;
            }
        }

        // Only an add can push the table over its threshold. Mirror JDK's addCount tail: while the
        // count is at/over the resize threshold and the table is not maxed out, either help an
        // ongoing resize (register + Transfer) or initiate a new one.
        if (x <= 0)
        {
            return;
        }
        while (true)
        {
            int sc = Volatile.Read(ref _sizeCtl);
            if (s < sc)
            {
                break;
            }
            Node?[] tab = _table;
            int n = tab.Length;
            if (n >= MaximumCapacity)
            {
                break;
            }
            int rs = ResizeStamp(n);
            if (sc < 0)
            {
                // A resize is already running for some generation; join it if it matches ours.
                Node?[]? nt = _nextTable;
                if ((sc >>> ResizeStampShift) != rs
                    || sc == (rs << ResizeStampShift) + 1
                    || sc == (rs << ResizeStampShift) + MaxResizers
                    || nt == null
                    || Volatile.Read(ref _transferIndex) <= 0)
                {
                    break;
                }
                if (Interlocked.CompareExchange(ref _sizeCtl, sc + 1, sc) == sc)
                {
                    Transfer(tab, nt);
                }
            }
            else if (Interlocked.CompareExchange(ref _sizeCtl, (rs << ResizeStampShift) + 2, sc) == sc)
            {
                // We are the initiator: install a fresh staging table and start the transfer.
                Transfer(tab, null);
            }
            s = SumCount();
        }
    }

    // Retry a rehashed cell on collision. The cell array is allocated once at the NCPU cap, so there
    // is nothing to grow into.
    private void FullAddCount(long x)
    {
        int probe = _cellProbe;
        if (probe == 0)
        {
            probe = InitProbe();
        }
        while (true)
        {
            long[]? cells = _counterCells;
            if (cells != null)
            {
                ref long cell = ref cells[LogicalCell(cells, probe)];
                long v = Interlocked.Read(ref cell);
                if (Interlocked.CompareExchange(ref cell, v + x, v) == v)
                {
                    break;
                }
                probe = AdvanceProbe(probe);
            }
            else if (Interlocked.CompareExchange(ref _cellsBusy, 1, 0) == 0)
            {
                try
                {
                    if (_counterCells == null)
                    {
                        int logicalCells = Math.Max(2, Common.CeilingPowerOfTwo(Environment.ProcessorCount));
                        _counterCells = new long[logicalCells * CounterCellStride];
                    }
                }
                finally
                {
                    Volatile.Write(ref _cellsBusy, 0);
                }
            }
            else
            {
                long b = Interlocked.Read(ref _baseCount);
                if (Interlocked.CompareExchange(ref _baseCount, b + x, b) == b)
                {
                    break;
                }
            }
        }
        _cellProbe = probe;
    }

    private static int InitProbe()
    {
        int probe = Environment.CurrentManagedThreadId * unchecked((int)0x9E3779B1);
        probe = AdvanceProbe(probe == 0 ? 1 : probe);
        _cellProbe = probe;
        return probe;
    }

    /// <summary>
    /// Faithful port of JDK's <c>transfer(tab, nextTab)</c>. Moves bins from <paramref name="oldTab"/>
    /// into a doubled table cooperatively: workers claim descending strides of bins via CAS on
    /// <see cref="_transferIndex"/>, migrate each claimed bin (splitting its chain into a lo half that
    /// stays at index <c>i</c> and a hi half that moves to <c>i + n</c>, using the lastRun reuse
    /// optimization), and mark the migrated bin with a <see cref="ForwardingNode"/>. The worker that
    /// runs out of claims decrements <see cref="_sizeCtl"/>; the one that brings it back to the "+2"
    /// base performs the final full recheck sweep and publishes <see cref="_table"/>.
    /// </summary>
    private void Transfer(Node?[] oldTab, Node?[]? nextTab)
    {
        int n = oldTab.Length;
        int stride = NCpu > 1 ? (n >>> 3) / NCpu : n;
        if (stride < MinTransferStride)
        {
            stride = MinTransferStride;
        }

        if (nextTab == null)
        {
            // Initiating worker allocates the staging table and resets the claim cursor.
            var nt = new Node?[n << 1];
            nextTab = nt;
            _nextTable = nt;
            Volatile.Write(ref _transferIndex, n);
        }

        var forward = new ForwardingNode(nextTab);
        bool advance = true;
        bool finishing = false;
        int i = 0;
        int bound = 0;

        while (true)
        {
            // Claim the next bin (i) within the current stride [bound, i]; grab a new stride when done.
            while (advance)
            {
                if (--i >= bound || finishing)
                {
                    advance = false;
                }
                else
                {
                    int nextIndex = Volatile.Read(ref _transferIndex);
                    if (nextIndex <= 0)
                    {
                        i = -1;
                        advance = false;
                    }
                    else
                    {
                        int nextBound = nextIndex > stride ? nextIndex - stride : 0;
                        if (Interlocked.CompareExchange(ref _transferIndex, nextBound, nextIndex) == nextIndex)
                        {
                            bound = nextBound;
                            i = nextIndex - 1;
                            advance = false;
                        }
                    }
                }
            }

            if (i < 0 || i >= n)
            {
                // This worker is out of bins. Decrement the resizer count; the last one publishes.
                int sc;
                if (finishing)
                {
                    _nextTable = null;
                    _table = nextTab;
                    Volatile.Write(ref _sizeCtl, (n << 1) - (n >>> 1)); // 0.75 * newCap
                    return;
                }
                sc = Volatile.Read(ref _sizeCtl);
                if (Interlocked.CompareExchange(ref _sizeCtl, sc - 1, sc) == sc)
                {
                    int rs = ResizeStamp(n);
                    if ((sc - 2) != (rs << ResizeStampShift))
                    {
                        return; // not the last worker
                    }
                    // Last worker: sweep every bin once more to confirm full migration before publish.
                    finishing = true;
                    advance = true;
                    i = n;
                }
                continue;
            }

            Node? f = TabAt(oldTab, i);
            if (f == null)
            {
                // Empty bin: mark forwarded so a late writer follows to nextTab (retries via HelpTransfer).
                advance = CasTabAt(oldTab, i, null, forward);
            }
            else if (f is ForwardingNode)
            {
                advance = true; // already migrated (by the finishing sweep or another worker)
            }
            else
            {
                lock (f)
                {
                    if (TabAt(oldTab, i) != f)
                    {
                        continue; // head changed under us; re-read
                    }
                    int runBit = f.Hash & n;
                    Node lastRun = f;
                    for (Node? p = f.Next; p != null; p = p.Next)
                    {
                        int b = p.Hash & n;
                        if (b != runBit)
                        {
                            runBit = b;
                            lastRun = p;
                        }
                    }
                    Node? lo;
                    Node? hi;
                    if (runBit == 0)
                    {
                        lo = lastRun;
                        hi = null;
                    }
                    else
                    {
                        hi = lastRun;
                        lo = null;
                    }
                    for (Node? p = f; p != lastRun; p = p.Next)
                    {
                        int ph = p!.Hash;
                        TKey pk = p.Key;
                        TValue pv = Volatile.Read(ref p.Value);
                        if ((ph & n) == 0)
                        {
                            lo = new Node(ph, pk, pv, lo);
                        }
                        else
                        {
                            hi = new Node(ph, pk, pv, hi);
                        }
                    }
                    SetTabAt(nextTab, i, lo);
                    SetTabAt(nextTab, i + n, hi);
                    SetTabAt(oldTab, i, forward);
                    advance = true;
                }
            }
        }
    }
}

