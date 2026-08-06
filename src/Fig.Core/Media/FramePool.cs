using System.Buffers;
using System.Runtime.CompilerServices;

namespace Fig.Core.Media
{
    /// <summary>
    /// Pool of transient full-frame pixel buffers. Effects and transitions rent an output
    /// buffer, the compositor consumes it within the same frame, then the caller returns it.
    /// <see cref="Return"/> is a safe no-op for buffers that were not rented here (source
    /// scratches and cache-owned copies), so callers may return unconditionally.
    /// </summary>
    public static class FramePool
    {
        // Reference-keyed set of buffers currently lent out, so Return is exactly-once and
        // never hands a non-pooled buffer (e.g. a cached frame's pixels) back to ArrayPool.
        private static readonly ConditionalWeakTable<byte[], byte[]> Rented = new();

        /// <summary>Rents a buffer of at least <paramref name="size"/> bytes, tracked as pooled.</summary>
        public static byte[] Rent(int size)
        {
            var buf = ArrayPool<byte>.Shared.Rent(size);
            Rented.AddOrUpdate(buf, buf);
            return buf;
        }

        /// <summary>Returns a rented buffer to the pool. No-op unless it was rented here.</summary>
        public static void Return(byte[] buf)
        {
            if (buf is not null && Rented.Remove(buf))
                ArrayPool<byte>.Shared.Return(buf);
        }
    }
}
