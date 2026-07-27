using BenchmarkDotNet.Attributes;
using Espresso.Internal;

namespace Espresso.Benchmarks;

/// <summary>
/// Benchmarks for the intrusive linked deque. These operations run single-threaded in the cache
/// (guarded by the eviction lock), so the benchmark is single-threaded and focuses on the hot LRU
/// reorder pattern (<c>MoveToBack</c>) plus offer/poll churn.
/// </summary>
[MemoryDiagnoser]
public class LinkedDequeBenchmarks
{
    private sealed class Node : IAccessOrder<Node>
    {
        private Node? _prev;
        private Node? _next;
        public Node? GetPreviousInAccessOrder() => _prev;
        public void SetPreviousInAccessOrder(Node? prev) => _prev = prev;
        public Node? GetNextInAccessOrder() => _next;
        public void SetNextInAccessOrder(Node? next) => _next = next;
    }

    [Params(10_000)]
    public int Size;

    private AccessOrderDeque<Node> _deque = null!;
    private Node[] _nodes = null!;

    [GlobalSetup]
    public void Setup()
    {
        _deque = new AccessOrderDeque<Node>();
        _nodes = new Node[Size];
        for (int i = 0; i < Size; i++)
        {
            _nodes[i] = new Node();
            _deque.OfferLast(_nodes[i]);
        }
    }

    /// <summary>The LRU hot path: touch each element, moving it to the back.</summary>
    [Benchmark]
    public void MoveToBack_All()
    {
        for (int i = 0; i < _nodes.Length; i++)
        {
            _deque.MoveToBack(_nodes[i]);
        }
    }

    /// <summary>Offer/poll churn against a fresh deque (isolates link maintenance cost).</summary>
    [Benchmark]
    public void OfferAndPoll_Churn()
    {
        var deque = new AccessOrderDeque<Node>();
        for (int i = 0; i < _nodes.Length; i++)
        {
            deque.OfferLast(_nodes[i]);
        }
        while (deque.Poll() != null) { }
    }
}