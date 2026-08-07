using System.Buffers;
using System.Collections.Generic;
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

        /// <summary>
        /// Makes a frame's pixels distinct from any buffer already seen this frame. A
        /// <see cref="VideoFrameSource"/> reuses one scratch buffer for every decode, so two
        /// clips referencing the same media file return the same byte array — blending them
        /// would blend a frame with itself. Copies into a pooled buffer (tracked in
        /// <paramref name="owned"/> so the caller returns it later) when an alias is found.
        /// </summary>
        public static void EnsureDistinct(DecodedFrame frame, HashSet<byte[]> seen, List<byte[]>? owned)
        {
            if (frame.Pixels is null || frame.Pixels.Length == 0 || seen.Add(frame.Pixels))
                return;
            var copy = Rent(frame.Pixels.Length);
            Buffer.BlockCopy(frame.Pixels, 0, copy, 0, frame.Pixels.Length);
            frame.Pixels = copy;
            owned?.Add(copy);
        }
    }
}
