using System;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Fig.Core.Media
{
    /// <summary>
    /// Shared pixel-kernel helpers. Two levers, both safe under the existing byte-exact tests:
    /// (1) <see cref="Rows"/> runs a kernel across all cores on large frames, and (2) SIMD
    /// fast paths for operations whose math is exactly equivalent to the scalar version
    /// (saturating add/subtract for brightness/invert).
    /// </summary>
    internal static class PixelOps
    {
        // Parallelize only once the frame is large enough to amortize the thread-pool overhead.
        private const int ParallelMinRows = 160;

        /// <summary>Benchmark/debug switch: force the scalar paths (disables SIMD).</summary>
        public static bool ForceScalar;

        /// <summary>Benchmark/debug switch: run kernels single-threaded (disables row parallelism).</summary>
        public static bool ForceSequential;

        /// <summary>Runs <paramref name="row"/> for every scanline, in parallel on larger frames.</summary>
        public static void Rows(int height, Action<int> row)
        {
            if (!ForceSequential && height >= ParallelMinRows)
            {
                var maxParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));
                System.Threading.Tasks.Parallel.For(0, height,
                    new System.Threading.Tasks.ParallelOptions { MaxDegreeOfParallelism = maxParallelism },
                    row);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    row(y);
            }
        }

        /// <summary>
        /// dst = clamp(src + delta) on the RGB channels, leaving alpha untouched. Exact for
        /// integer deltas (the scalar path clamps to 0..255, the SIMD path saturates).
        /// </summary>
        public static unsafe void AddSaturateRgb(byte[] dst, byte[] src, int delta)
        {
            if (delta == 0)
            {
                Buffer.BlockCopy(src, 0, dst, 0, src.Length);
                return;
            }

            if (ForceScalar)
            {
                for (var i = 0; i < src.Length; i += 4)
                {
                    dst[i] = ClampByte(src[i] + delta);
                    dst[i + 1] = ClampByte(src[i + 1] + delta);
                    dst[i + 2] = ClampByte(src[i + 2] + delta);
                    dst[i + 3] = src[i + 3];
                }
                return;
            }

            if (delta == 0)
            {
                Buffer.BlockCopy(src, 0, dst, 0, src.Length);
                return;
            }

            if (Avx2.IsSupported)
            {
                var keepRgb = Vector256.Create(0x00FFFFFFu).AsByte();   // alpha lane zeroed
                var keepA = Vector256.Create(0xFF000000u).AsByte();     // only alpha kept
                fixed (byte* d = dst)
                fixed (byte* s = src)
                {
                    var i = 0;
                    if (delta > 0)
                    {
                        var add = Vector256.Create((byte)delta);
                        for (; i + 32 <= src.Length; i += 32)
                        {
                            var v = Avx.LoadDquVector256(s + i);
                            var added = Avx2.AddSaturate(v, add);
                            Avx.Store(d + i, Avx2.Or(Avx2.And(added, keepRgb), Avx2.And(v, keepA)));
                        }
                    }
                    else
                    {
                        var sub = Vector256.Create((byte)(-delta));
                        for (; i + 32 <= src.Length; i += 32)
                        {
                            var v = Avx.LoadDquVector256(s + i);
                            var added = Avx2.SubtractSaturate(v, sub);
                            Avx.Store(d + i, Avx2.Or(Avx2.And(added, keepRgb), Avx2.And(v, keepA)));
                        }
                    }
                    for (; i < src.Length; i++)
                        dst[i] = ClampByte(src[i] + (i % 4 == 3 ? 0 : delta));
                }
            }
            else
            {
                for (var i = 0; i < src.Length; i += 4)
                {
                    dst[i] = ClampByte(src[i] + delta);
                    dst[i + 1] = ClampByte(src[i + 1] + delta);
                    dst[i + 2] = ClampByte(src[i + 2] + delta);
                    dst[i + 3] = src[i + 3];
                }
            }
        }

        /// <summary>dst = 255 - src on the RGB channels (full invert), alpha untouched. Exact.</summary>
        public static unsafe void InvertRgbFull(byte[] dst, byte[] src)
        {
            if (ForceScalar)
            {
                for (var i = 0; i < src.Length; i += 4)
                {
                    dst[i] = (byte)(255 - src[i]);
                    dst[i + 1] = (byte)(255 - src[i + 1]);
                    dst[i + 2] = (byte)(255 - src[i + 2]);
                    dst[i + 3] = src[i + 3];
                }
                return;
            }

            if (Avx2.IsSupported)
            {
                var keepRgb = Vector256.Create(0x00FFFFFFu).AsByte();
                var keepA = Vector256.Create(0xFF000000u).AsByte();
                var max = Vector256.Create(byte.MaxValue);
                fixed (byte* d = dst)
                fixed (byte* s = src)
                {
                    var i = 0;
                    for (; i + 32 <= src.Length; i += 32)
                    {
                        var v = Avx.LoadDquVector256(s + i);
                        var inv = Avx2.SubtractSaturate(max, v);
                        Avx.Store(d + i, Avx2.Or(Avx2.And(inv, keepRgb), Avx2.And(v, keepA)));
                    }
                    for (; i < src.Length; i++)
                        dst[i] = i % 4 == 3 ? src[i] : (byte)(255 - src[i]);
                }
            }
            else
            {
                for (var i = 0; i < src.Length; i += 4)
                {
                    dst[i] = (byte)(255 - src[i]);
                    dst[i + 1] = (byte)(255 - src[i + 1]);
                    dst[i + 2] = (byte)(255 - src[i + 2]);
                    dst[i + 3] = src[i + 3];
                }
            }
        }

        private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);
    }
}
