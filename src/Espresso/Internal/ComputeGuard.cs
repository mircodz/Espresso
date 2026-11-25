using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Espresso.Internal;

/// <summary>
/// Records, per thread, the <see cref="ConcurrentHashMap{TKey,TValue}"/> instances whose mapping
/// functions are running on the thread. A mapping function runs while its map's bin lock is held, and
/// monitors are reentrant, so a write from inside the function must not start or help a resize of
/// that map: the transfer would reach the bin the outer operation holds and either meet its
/// reservation or migrate the list the outer operation is about to append to. The map instead leaves
/// the resize to the outer operation, whose own size update runs after the function returns.
/// <para>
/// Scopes nest (a function of map A may call map B, whose function writes to A), so the thread keeps
/// a small stack rather than a single slot. It holds integer map ids rather than references, so
/// entering and leaving a scope stores no reference (no GC write barrier). Like
/// <see cref="ThreadProbe"/>, the thread-static lives on a non-generic class so that the JIT inlines
/// its access.
/// </para>
/// </summary>
internal static class ComputeGuard
{
    private static int s_lastId;

    [ThreadStatic] private static Frames? t_frames;

    /// <summary>
    /// Returns an id for a new map. Ids repeat only after 2^32 maps; a map sharing an id with another
    /// map whose function is running would merely postpone its resize to a later write.
    /// </summary>
    public static int NewId() => Interlocked.Increment(ref s_lastId);

    /// <summary>
    /// Marks the mapping function of the map with <paramref name="id"/> as running until
    /// <see cref="Frames.Exit"/> is called on the result.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Frames Enter(int id)
    {
        Frames frames = t_frames ?? Initialize();
        frames.Push(id);
        return frames;
    }

    /// <summary>Whether a mapping function of the map with <paramref name="id"/> is running on this thread.</summary>
    public static bool IsActive(int id)
    {
        Frames? frames = t_frames;
        return frames != null && frames.Contains(id);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Frames Initialize() => t_frames = new Frames();

    internal sealed class Frames
    {
        private int[] _ids = new int[4];
        private int _depth;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Push(int id)
        {
            int depth = _depth;
            int[] ids = _ids;
            if ((uint)depth < (uint)ids.Length)
            {
                ids[depth] = id;
                _depth = depth + 1;
            }
            else
            {
                Grow(id);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Exit() => _depth--;

        internal bool Contains(int id)
        {
            int[] ids = _ids;
            for (int i = _depth - 1; i >= 0; i--)
            {
                if (ids[i] == id)
                {
                    return true;
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void Grow(int id)
        {
            Array.Resize(ref _ids, _ids.Length * 2);
            _ids[_depth++] = id;
        }
    }
}
