using System;
using System.Collections.Generic;
using System.Linq;
using Espresso.Internal;
using Xunit;

namespace Espresso.Tests;

public sealed class LinkedDequeTest
{
    private sealed class Element : IAccessOrder<Element>
    {
        public readonly int Id;
        private Element? _prev;
        private Element? _next;
        public Element(int id) => Id = id;

        public Element? GetPreviousInAccessOrder() => _prev;
        public void SetPreviousInAccessOrder(Element? prev) => _prev = prev;
        public Element? GetNextInAccessOrder() => _next;
        public void SetNextInAccessOrder(Element? next) => _next = next;
        public override string ToString() => $"E{Id}";
    }

    private static (AccessOrderDeque<Element> deque, List<Element> items) Populate(int n)
    {
        var deque = new AccessOrderDeque<Element>();
        var items = new List<Element>();
        for (int i = 0; i < n; i++)
        {
            var e = new Element(i);
            items.Add(e);
            Assert.True(deque.OfferLast(e));
        }
        return (deque, items);
    }

    [Fact]
    public void Empty()
    {
        var deque = new AccessOrderDeque<Element>();
        Assert.True(deque.IsEmpty);
        Assert.Equal(0, deque.Count);
        Assert.Null(deque.PeekFirst);
        Assert.Null(deque.PeekLast);
        Assert.Null(deque.Poll());
    }

    [Fact]
    public void OfferLast_AppendsInOrder()
    {
        var (deque, items) = Populate(5);
        Assert.False(deque.IsEmpty);
        Assert.Equal(5, deque.Count);
        Assert.Same(items[0], deque.PeekFirst);
        Assert.Same(items[4], deque.PeekLast);
        Assert.Equal(items, deque.ToList());
    }

    [Fact]
    public void OfferFirst_Prepends()
    {
        var deque = new AccessOrderDeque<Element>();
        var a = new Element(1);
        var b = new Element(2);
        deque.OfferFirst(a);
        deque.OfferFirst(b);
        Assert.Same(b, deque.PeekFirst);
        Assert.Same(a, deque.PeekLast);
    }

    [Fact]
    public void Offer_DuplicateRejected()
    {
        var deque = new AccessOrderDeque<Element>();
        var e = new Element(1);
        Assert.True(deque.OfferLast(e));
        Assert.False(deque.OfferLast(e));
        Assert.False(deque.OfferFirst(e));
        Assert.Equal(1, deque.Count);
    }

    [Fact]
    public void Offer_Null_Throws()
        => Assert.Throws<ArgumentNullException>(() => new AccessOrderDeque<Element>().OfferLast(null!));

    [Fact]
    public void Contains_FastPath()
    {
        var (deque, items) = Populate(3);
        Assert.True(deque.Contains(items[0]));
        Assert.True(deque.Contains(items[1]));
        Assert.True(deque.Contains(items[2]));
        Assert.False(deque.Contains(new Element(99)));
    }

    [Fact]
    public void PollFirst_And_PollLast()
    {
        var (deque, items) = Populate(3);
        Assert.Same(items[0], deque.PollFirst());
        Assert.Same(items[2], deque.PollLast());
        Assert.Equal(1, deque.Count);
        Assert.Same(items[1], deque.PeekFirst);
    }

    [Fact]
    public void Poll_ToEmpty()
    {
        var (deque, _) = Populate(4);
        while (deque.Poll() != null) { }
        Assert.True(deque.IsEmpty);
        Assert.Null(deque.PeekFirst);
        Assert.Null(deque.PeekLast);
    }

    [Fact]
    public void Remove_Middle_RelinksNeighbors()
    {
        var (deque, items) = Populate(3);
        Assert.True(deque.Remove(items[1]));
        Assert.Equal(2, deque.Count);
        Assert.Equal(new[] { items[0], items[2] }, deque.ToList());
        Assert.False(deque.Contains(items[1]));
        Assert.False(deque.Remove(items[1]));
    }

    [Fact]
    public void MoveToBack_And_MoveToFront()
    {
        var (deque, items) = Populate(3);
        deque.MoveToBack(items[0]);
        Assert.Equal(new[] { items[1], items[2], items[0] }, deque.ToList());
        deque.MoveToFront(items[0]);
        Assert.Equal(new[] { items[0], items[1], items[2] }, deque.ToList());
    }

    [Fact]
    public void MoveToBack_OnLast_IsNoOp()
    {
        var (deque, items) = Populate(3);
        deque.MoveToBack(items[2]);
        Assert.Equal(items, deque.ToList());
    }

    [Fact]
    public void IsFirst_IsLast()
    {
        var (deque, items) = Populate(3);
        Assert.True(deque.IsFirst(items[0]));
        Assert.False(deque.IsFirst(items[1]));
        Assert.True(deque.IsLast(items[2]));
        Assert.False(deque.IsLast(items[1]));
        Assert.False(deque.IsFirst(null));
    }

    [Fact]
    public void Clear_UnlinksEverything()
    {
        var (deque, items) = Populate(3);
        deque.Clear();
        Assert.True(deque.IsEmpty);
        foreach (var e in items)
        {
            Assert.Null(e.GetPreviousInAccessOrder());
            Assert.Null(e.GetNextInAccessOrder());
            Assert.False(deque.Contains(e));
        }
    }

    [Fact]
    public void DescendingEnumerator_ReversesOrder()
    {
        var (deque, items) = Populate(4);
        var reversed = new List<Element>();
        var e = deque.GetDescendingEnumerator();
        while (e.MoveNext())
        {
            reversed.Add(e.Current);
        }
        Assert.Equal(Enumerable.Reverse(items), reversed);
    }

    [Fact]
    public void Enumerator_FailsFast_OnStructuralModification()
    {
        var (deque, _) = Populate(3);
        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var _ in deque)
            {
                deque.OfferLast(new Element(100));
            }
        });
    }
}
