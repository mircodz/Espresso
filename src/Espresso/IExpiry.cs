using System;

namespace Espresso;

/// <summary>
/// Calculates when cache entries expire, allowing a distinct duration for each entry. A single
/// expiration is retained per entry and it is reset after every create, update, or read as directed
/// by this calculator. An entry is expired lazily on access and eagerly during maintenance.
/// <para>
/// All durations are expressed in nanoseconds relative to the cache's ticker (100 ns == 1 tick).
/// Return <see cref="long.MaxValue"/> for "effectively never" — the value is clamped internally to a
/// maximum of ~150 years so it never wraps.
/// </para>
/// </summary>
/// <typeparam name="K">the key type.</typeparam>
/// <typeparam name="V">the value type.</typeparam>
public interface IExpiry<in K, in V>
    where K : notnull
    where V : class
{
    /// <summary>
    /// Specifies that the entry should be automatically removed from the cache once the duration (in
    /// nanoseconds) has elapsed after the entry's creation.
    /// </summary>
    /// <param name="key">the key of the created entry.</param>
    /// <param name="value">the value of the created entry.</param>
    /// <param name="currentTime">the current time, in nanoseconds.</param>
    /// <returns>the length of time (in nanoseconds) before the entry expires.</returns>
    long ExpireAfterCreate(K key, V value, long currentTime);

    /// <summary>
    /// Specifies that the entry should be automatically removed from the cache once the duration (in
    /// nanoseconds) has elapsed after the replacement of its value.
    /// </summary>
    /// <param name="key">the key of the updated entry.</param>
    /// <param name="value">the value of the updated entry.</param>
    /// <param name="currentTime">the current time, in nanoseconds.</param>
    /// <param name="currentDuration">the current duration, in nanoseconds, until the entry expires.</param>
    /// <returns>the length of time (in nanoseconds) before the entry expires.</returns>
    long ExpireAfterUpdate(K key, V value, long currentTime, long currentDuration);

    /// <summary>
    /// Specifies that the entry should be automatically removed from the cache once the duration (in
    /// nanoseconds) has elapsed after its last read.
    /// </summary>
    /// <param name="key">the key of the read entry.</param>
    /// <param name="value">the value of the read entry.</param>
    /// <param name="currentTime">the current time, in nanoseconds.</param>
    /// <param name="currentDuration">the current duration, in nanoseconds, until the entry expires.</param>
    /// <returns>the length of time (in nanoseconds) before the entry expires.</returns>
    long ExpireAfterRead(K key, V value, long currentTime, long currentDuration);
}

/// <summary>
/// An <see cref="IExpiry{K,V}"/> that computes each entry's lifetime from a delegate returning a
/// <see cref="TimeSpan"/>. The same duration is used after create, update, and read unless a specific
/// delegate is supplied.
/// </summary>
internal sealed class FuncExpiry<K, V> : IExpiry<K, V>
    where K : notnull
    where V : class
{
    private readonly Func<K, V, TimeSpan> _create;
    private readonly Func<K, V, TimeSpan>? _update;
    private readonly Func<K, V, TimeSpan>? _read;

    /// <summary>Creates an expiry using a single duration function for create, update, and read.</summary>
    public FuncExpiry(Func<K, V, TimeSpan> afterAny)
    {
        ArgumentNullException.ThrowIfNull(afterAny);
        _create = afterAny;
    }

    /// <summary>
    /// Creates an expiry with independent durations. A null <paramref name="afterUpdate"/> or
    /// <paramref name="afterRead"/> leaves the current duration unchanged for that event.
    /// </summary>
    public FuncExpiry(
        Func<K, V, TimeSpan> afterCreate,
        Func<K, V, TimeSpan>? afterUpdate,
        Func<K, V, TimeSpan>? afterRead)
    {
        ArgumentNullException.ThrowIfNull(afterCreate);
        _create = afterCreate;
        _update = afterUpdate;
        _read = afterRead;
    }

    private static long ToNanos(TimeSpan duration) => duration.Ticks * 100L;

    /// <inheritdoc/>
    public long ExpireAfterCreate(K key, V value, long currentTime)
        => ToNanos(_create(key, value));

    /// <inheritdoc/>
    public long ExpireAfterUpdate(K key, V value, long currentTime, long currentDuration)
        => _update != null ? ToNanos(_update(key, value)) : currentDuration;

    /// <inheritdoc/>
    public long ExpireAfterRead(K key, V value, long currentTime, long currentDuration)
        => _read != null ? ToNanos(_read(key, value)) : currentDuration;
}
