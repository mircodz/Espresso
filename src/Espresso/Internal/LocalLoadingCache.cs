using System;
using System.Collections.Generic;
using System.Reflection;
using Espresso.Stats;

namespace Espresso.Internal;

/// <summary>
/// The bulk-loading behavior shared by the bounded and unbounded loading caches (Caffeine's
/// <c>LocalLoadingCache.getAll</c> and <c>LocalManualCache.bulkLoad</c>).
/// </summary>
internal static class LocalLoadingCache
{
    /// <summary>
    /// Returns whether the loader overrides <see cref="ICacheLoader{K,V}.LoadAll"/>. The default
    /// implementation loads key by key, so a cache uses its own sequential path instead of calling it.
    /// </summary>
    public static bool HasLoadAll<K, V>(ICacheLoader<K, V> loader)
        where K : notnull
        where V : class
    {
        Type contract = typeof(ICacheLoader<K, V>);
        try
        {
            InterfaceMapping map = loader.GetType().GetInterfaceMap(contract);
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == nameof(ICacheLoader<K, V>.LoadAll))
                {
                    return map.TargetMethods[i].DeclaringType != contract;
                }
            }
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or InvalidOperationException)
        {
            // The mapping is unavailable (e.g. a dynamic proxy): use the sequential path.
        }
        return false;
    }

    /// <summary>
    /// Returns the values for the unique keys, loading the absent ones: in one
    /// <see cref="ICacheLoader{K,V}.LoadAll"/> call when <paramref name="bulk"/>, otherwise one key at
    /// a time. Every key is validated before anything is read or loaded.
    /// </summary>
    public static IReadOnlyDictionary<K, V> GetAll<K, V>(ILoadingCache<K, V> cache, ICacheLoader<K, V> loader,
        bool bulk, StatsCounter statsCounter, Ticker statsTicker, IEnumerable<K> keys)
        where K : notnull
        where V : class
    {
        ArgumentNullException.ThrowIfNull(keys);
        var unique = new List<K>();
        var seen = new HashSet<K>();
        foreach (K key in keys)
        {
            if (key is null)
            {
                throw new ArgumentNullException(nameof(key));
            }
            if (seen.Add(key))
            {
                unique.Add(key);
            }
        }
        return bulk
            ? BulkLoad(cache, loader, statsCounter, statsTicker, unique)
            : LoadSequentially(cache, statsCounter, unique);
    }

    /// <summary>
    /// Loads each key in order. If a load throws, the keys not yet reached are recorded as misses
    /// before the failure propagates.
    /// </summary>
    private static Dictionary<K, V> LoadSequentially<K, V>(ILoadingCache<K, V> cache, StatsCounter statsCounter, List<K> keys)
        where K : notnull
        where V : class
    {
        var result = new Dictionary<K, V>(keys.Count);
        int count = 0;
        try
        {
            foreach (K key in keys)
            {
                count++;
                V? value = cache.Get(key);
                if (value != null)
                {
                    result[key] = value;
                }
            }
        }
        catch
        {
            statsCounter.RecordMisses(keys.Count - count);
            throw;
        }
        return result;
    }

    /// <summary>
    /// Reads the present keys, then loads the absent ones in one call. Every loaded entry is cached,
    /// including keys that were not requested; a requested key the loader omits stays absent.
    /// </summary>
    private static Dictionary<K, V> BulkLoad<K, V>(ILoadingCache<K, V> cache, ICacheLoader<K, V> loader,
        StatsCounter statsCounter, Ticker statsTicker, List<K> keys)
        where K : notnull
        where V : class
    {
        IReadOnlyDictionary<K, V> present = cache.GetAllPresent(keys);
        if (present.Count == keys.Count)
        {
            return new Dictionary<K, V>(present);
        }
        var keysToLoad = new List<K>(keys.Count - present.Count);
        foreach (K key in keys)
        {
            if (!present.ContainsKey(key))
            {
                keysToLoad.Add(key);
            }
        }

        long start = statsTicker.Read();
        bool success = false;
        var result = new Dictionary<K, V>(keys.Count);
        try
        {
            IReadOnlyDictionary<K, V> loaded = loader.LoadAll(keysToLoad.AsReadOnly())
                ?? throw new InvalidOperationException("The loader returned a null map");
            foreach (KeyValuePair<K, V> entry in loaded)
            {
                cache.Put(entry.Key, entry.Value);
            }
            // In the requested order; a requested key the loader omitted stays absent.
            foreach (K key in keys)
            {
                if (present.TryGetValue(key, out V? value) || loaded.TryGetValue(key, out value))
                {
                    result[key] = value;
                }
            }
            success = loaded.Count != 0;
        }
        finally
        {
            long loadTime = statsTicker.Read() - start;
            if (success)
            {
                statsCounter.RecordLoadSuccess(loadTime);
            }
            else
            {
                statsCounter.RecordLoadFailure(loadTime);
            }
        }
        return result;
    }
}
