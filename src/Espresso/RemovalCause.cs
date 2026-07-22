namespace Espresso;

/// <summary>The reason why a cached entry was removed.</summary>
public enum RemovalCause
{
    /// <summary>The entry was manually removed by the user (invalidate, remove, replace-to-absent).</summary>
    Explicit,

    /// <summary>The entry was not removed but its value was replaced by the user (put, replace).</summary>
    Replaced,

    /// <summary>The entry's key or value was garbage-collected (weak/soft references).</summary>
    Collected,

    /// <summary>The entry's expiration timestamp passed (expire-after-write/access/var).</summary>
    Expired,

    /// <summary>The entry was evicted due to size or weight constraints.</summary>
    Size,
}

/// <summary>Extension helpers for <see cref="RemovalCause"/>.</summary>
public static class RemovalCauseExtensions
{
    /// <summary>
    /// Returns whether the removal was an automatic eviction (neither
    /// <see cref="RemovalCause.Explicit"/> nor <see cref="RemovalCause.Replaced"/>).
    /// </summary>
    public static bool WasEvicted(this RemovalCause cause)
        => cause is not (RemovalCause.Explicit or RemovalCause.Replaced);
}
