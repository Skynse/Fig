using System;
using System.Runtime.InteropServices;
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
    /// <summary>What one colour becomes, for <see cref="PixelOps.MapColor{T}"/>. Channels are 0–255.</summary>
    internal interface IColorMap
    {
        void Map(ref double r, ref double g, ref double b);
    }

    internal static class PixelOps
    {
        // Parallelize only once the frame is large enough to amortize the thread-pool overhead.
        private const int ParallelMinRows = 160;

        /// <summary>Benchmark/debug switch: force the scalar paths (disables SIMD).</summary>
        public static bool ForceScalar;

        /// <summary>Benchmark/debug switch: run kernels single-threaded (disables row parallelism).</summary>
        public static bool ForceSequential;

        /// <summary>
        /// A colour change made pixel by pixel: <paramref name="map"/> says what one colour (0–255 per channel) becomes;
        /// this does the rest: a new frame, the rows in parallel, rounding and clamping, alpha kept. A struct, so the
        /// per-pixel call inlines.
        /// </summary>
        public static DecodedFrame MapColor<T>(DecodedFrame frame, T map) where T : struct, IColorMap
        {
            var src = frame.Pixels;
            var w = frame.Width;
            var h = frame.Height;
            var px = FramePool.Rent(w * h * 4);
            Rows(h, y =>
            {
                var row = y * w * 4;
                for (var x = 0; x < w; x++)
                {
                    var i = row + x * 4;
                    double b = src[i], g = src[i + 1], r = src[i + 2];
                    map.Map(ref r, ref g, ref b);
                    px[i] = ToByte(b);
                    px[i + 1] = ToByte(g);
                    px[i + 2] = ToByte(r);
                    px[i + 3] = src[i + 3];
                }
            });
            return new DecodedFrame { Width = w, Height = h, Pixels = px };
        }

        /// <summary>
        /// A picture rearranged: each pixel of the new frame is the source pixel at column
        /// <paramref name="sourceX"/>[x] of row <paramref name="sourceY"/>(y). (A flip, a mosaic...)
        /// </summary>
        public static DecodedFrame Remap(DecodedFrame frame, int[] sourceX, Func<int, int> sourceY)
        {
            var w = frame.Width;
            var h = frame.Height;
            var src = frame.Pixels;
            var px = FramePool.Rent(w * h * 4);
            Rows(h, y =>
            {
                var from = MemoryMarshal.Cast<byte, uint>(src.AsSpan(sourceY(y) * w * 4, w * 4));
                var to = MemoryMarshal.Cast<byte, uint>(px.AsSpan(y * w * 4, w * 4));
                for (var x = 0; x < w; x++)
                    to[x] = from[sourceX[x]];
            });
            return new DecodedFrame { Width = w, Height = h, Pixels = px };
        }

        /// <summary>A channel value rounded and held to 0–255.</summary>
        public static byte ToByte(double v) => (byte)Math.Clamp((int)Math.Round(v), 0, 255);

        /// <summary>Sets every pixel's alpha to 255 in place (see <see cref="DecodedFrame.MakeOpaque"/>).</summary>
        public static void ForceOpaque(DecodedFrame frame)
        {
            var pixels = frame.Pixels;
            var stride = frame.Width * 4;
            Rows(frame.Height, y =>
            {
                var end = y * stride + stride;
                for (var i = y * stride + 3; i < end; i += 4)
                    pixels[i] = 255;
            });
        }

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

            // (byte)(-delta) below would silently truncate past 255 — keep the SIMD path
            // exact by falling back to scalar math for out-of-range deltas.
            if (delta is > 255 or < -255)
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

        /// <summary>
        /// dst = (a * (256 - w) + b * w + 128) >> 8 over <paramref name="count"/> bytes from <paramref name="offset"/>
        /// (w in 0..256). Whole segments at 0 or 256 are plain copies. Within one level of the exact blend.
        /// </summary>
        public static unsafe void LerpBytes(byte[] dst, byte[] a, byte[] b, int offset, int count, int w256)
        {
            if (count <= 0)
                return;
            if (w256 <= 0)
            {
                Buffer.BlockCopy(a, offset, dst, offset, count);
                return;
            }
            if (w256 >= 256)
            {
                Buffer.BlockCopy(b, offset, dst, offset, count);
                return;
            }

            var inv = 256 - w256;
            fixed (byte* pd = dst)
            fixed (byte* pa = a)
            fixed (byte* pb = b)
            {
                var i = offset;
                var end = offset + count;
                if (Avx2.IsSupported && !ForceScalar)
                {
                    var zero = Vector256<byte>.Zero;
                    var wa = Vector256.Create((ushort)inv);
                    var wb = Vector256.Create((ushort)w256);
                    var round = Vector256.Create((ushort)128);
                    for (; i + 32 <= end; i += 32)
                    {
                        var av = Avx.LoadDquVector256(pa + i);
                        var bv = Avx.LoadDquVector256(pb + i);
                        var lo = Avx2.ShiftRightLogical(Avx2.Add(Avx2.Add(
                            Avx2.MultiplyLow(Avx2.UnpackLow(av, zero).AsUInt16(), wa),
                            Avx2.MultiplyLow(Avx2.UnpackLow(bv, zero).AsUInt16(), wb)), round), 8);
                        var hi = Avx2.ShiftRightLogical(Avx2.Add(Avx2.Add(
                            Avx2.MultiplyLow(Avx2.UnpackHigh(av, zero).AsUInt16(), wa),
                            Avx2.MultiplyLow(Avx2.UnpackHigh(bv, zero).AsUInt16(), wb)), round), 8);
                        Avx.Store(pd + i, Avx2.PackUnsignedSaturate(lo.AsInt16(), hi.AsInt16()));
                    }
                }
                for (; i < end; i++)
                    pd[i] = (byte)((pa[i] * inv + pb[i] * w256 + 128) >> 8);
            }
        }

        /// <summary>dst = (src * k + 128) >> 8 over the whole buffer (k in 0..256): a brightness fade.</summary>
        public static unsafe void ScaleBytes(byte[] dst, byte[] src, int length, int k256)
        {
            if (k256 >= 256)
            {
                Buffer.BlockCopy(src, 0, dst, 0, length);
                return;
            }
            if (k256 <= 0)
            {
                Array.Clear(dst, 0, length);
                return;
            }
            fixed (byte* pd = dst)
            fixed (byte* ps = src)
            {
                var i = 0;
                if (Avx2.IsSupported && !ForceScalar)
                {
                    var zero = Vector256<byte>.Zero;
                    var k = Vector256.Create((ushort)k256);
                    var round = Vector256.Create((ushort)128);
                    for (; i + 32 <= length; i += 32)
                    {
                        var v = Avx.LoadDquVector256(ps + i);
                        var lo = Avx2.ShiftRightLogical(Avx2.Add(Avx2.MultiplyLow(Avx2.UnpackLow(v, zero).AsUInt16(), k), round), 8);
                        var hi = Avx2.ShiftRightLogical(Avx2.Add(Avx2.MultiplyLow(Avx2.UnpackHigh(v, zero).AsUInt16(), k), round), 8);
                        Avx.Store(pd + i, Avx2.PackUnsignedSaturate(lo.AsInt16(), hi.AsInt16()));
                    }
                }
                for (; i < length; i++)
                    pd[i] = (byte)((ps[i] * k256 + 128) >> 8);
            }
        }

        /// <summary>
        /// Blends two frames with a different weight per column (wipe, curtains): the same weights apply to
        /// every row, so columns are grouped into runs of equal weight and each run is one kernel call.
        /// </summary>
        public static void ColumnBlend(byte[] dst, byte[] a, byte[] b, int width, int height, int[] weight256)
        {
            var runs = new System.Collections.Generic.List<(int X0, int X1, int W)>();
            var start = 0;
            for (var x = 1; x <= width; x++)
            {
                if (x == width || weight256[x] != weight256[start])
                {
                    runs.Add((start, x, weight256[start]));
                    start = x;
                }
            }

            Rows(height, y =>
            {
                var row = y * width * 4;
                foreach (var (x0, x1, w) in runs)
                    LerpBytes(dst, a, b, row + x0 * 4, (x1 - x0) * 4, w);
            });
        }

        /// <summary>Weight (0..256) for a mix in 0..1.</summary>
        public static int Weight256(double mix) => (int)Math.Clamp(Math.Round(mix * 256), 0, 256);

        private static byte ClampByte(int v) => (byte)Math.Clamp(v, 0, 255);
    }
}
