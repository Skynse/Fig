using System;
using System.Collections.Generic;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Fig.Core.Media
{
    /// <summary>A single image layer to composite, in BGRA32 bottom-up pixel order.</summary>
    public sealed class CompositeLayer
    {
        public DecodedFrame? Frame { get; set; }
        public double Opacity { get; set; } = 1.0;
        public RectI? Crop { get; set; }
    }

    /// <summary>Integer pixel rectangle (in source pixel space) for cropping a layer.</summary>
    public readonly struct RectI
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public RectI(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }
    }

    /// <summary>
    /// Composites image layers onto a canvas using the painters algorithm. The list is
    /// ordered topmost-first, so the first layer is the top of the stack. Layers are blended
    /// bottom-to-top so the top layer is applied last and wins (covers the layers below).
    /// Each layer is alpha-blended by its opacity. All layers share the canvas dimensions.
    /// </summary>
    public static class FrameCompositor
    {
        public static DecodedFrame Compose(IReadOnlyList<CompositeLayer> layers, int width, int height)
        {
            var pixels = new byte[width * height * 4];
            byte[]? cropScratch = null;
            ComposeInto(layers, width, height, pixels, ref cropScratch);
            return new DecodedFrame { Width = width, Height = height, Pixels = pixels };
        }

        /// <summary>
        /// Composites into a caller-provided buffer (avoiding per-frame allocation).
        /// The buffer must be at least <c>width * height * 4</c> bytes; it is treated as BGRA.
        /// <paramref name="cropScratch"/> is a caller-owned reusable buffer for crop scaling —
        /// pass <c>null</c> and a fresh one is allocated when a crop is present.
        /// </summary>
        public static void ComposeInto(IReadOnlyList<CompositeLayer> layers, int width, int height, byte[] pixels,
            ref byte[]? cropScratch)
        {
            // start with an opaque black canvas
            Array.Clear(pixels, 0, pixels.Length);
            for (var i = 3; i < pixels.Length; i += 4)
                pixels[i] = 255;

            // layers are topmost-first: blend bottom-to-top so the first (topmost) layer
            // is applied last and wins over the layers below it
            for (var li = layers.Count - 1; li >= 0; li--)
            {
                var layer = layers[li];
                if (layer.Frame is null)
                    continue;
                if (layer.Frame.Width != width || layer.Frame.Height != height)
                    continue;
                if (layer.Opacity <= 0)
                    continue;

                var src = layer.Frame.Pixels;
                if (layer.Crop is { } crop && IsMeaningfulCrop(crop, width, height))
                {
                    if (cropScratch is null || cropScratch.Length < width * height * 4)
                        cropScratch = new byte[width * height * 4];
                    CropScaleNearest(src, width, height, crop, cropScratch);
                    src = cropScratch;
                }

                var opacity = (byte)Math.Clamp((int)Math.Round(layer.Opacity * 255), 0, 255);

                if (opacity >= 255)
                    BlendOpaque(pixels, src, width, height);
                else
                    BlendAlpha(pixels, src, width, height, opacity);
            }
        }

        private static bool IsMeaningfulCrop(RectI crop, int width, int height)
        {
            if (crop.Width <= 0 || crop.Height <= 0)
                return false;
            if (crop.X <= 0 && crop.Y <= 0 && crop.Width >= width && crop.Height >= height)
                return false;
            return true;
        }

        /// <summary>
        /// Scales the cropped source region into a full-size BGRA buffer (nearest-neighbor).
        /// Source/dest are bottom-up row order.
        /// </summary>
        private static void CropScaleNearest(byte[] src, int width, int height, RectI crop, byte[] dst)
        {
            var cx = Math.Clamp(crop.X, 0, Math.Max(0, width - 1));
            var cy = Math.Clamp(crop.Y, 0, Math.Max(0, height - 1));
            var cw = Math.Clamp(crop.Width, 1, width - cx);
            var ch = Math.Clamp(crop.Height, 1, height - cy);

            PixelOps.Rows(height, y =>
            {
                var srcY = cy + y * ch / height;
                // bottom-up: row 0 is the bottom of the image
                var srcRow = (height - 1 - srcY) * width * 4;
                var dstRow = (height - 1 - y) * width * 4;
                for (var x = 0; x < width; x++)
                {
                    var srcX = cx + x * cw / width;
                    var si = srcRow + srcX * 4;
                    var di = dstRow + x * 4;
                    dst[di] = src[si];
                    dst[di + 1] = src[si + 1];
                    dst[di + 2] = src[si + 2];
                    dst[di + 3] = src[si + 3];
                }
            });
        }

        private static unsafe void BlendOpaque(byte[] dst, byte[] src, int width, int height)
        {
            var total = width * height * 4;
            if (Avx2.IsSupported)
            {
                fixed (byte* d = dst)
                fixed (byte* s = src)
                {
                    var i = 0;
                    for (; i + 32 <= total; i += 32)
                        Avx.Store(d + i, Avx.LoadDquVector256(s + i));
                    for (; i < total; i++)
                        d[i] = s[i];
                }
                return;
            }

            Buffer.BlockCopy(src, 0, dst, 0, total);
        }

        private static unsafe void BlendAlpha(byte[] dst, byte[] src, int width, int height, byte alpha)
        {
            var total = width * height * 4; // total number of pixels for 4 channels (RGBA)
            if (Avx2.IsSupported)
            {
                var inv = (byte)(255 - alpha); // inverse alpha
                var alphaV = Vector256.Create((ushort)alpha); // alpha value as a vector
                var invV = Vector256.Create((ushort)inv); // inverse alpha value as a vector
                var magicV = Vector256.Create((ushort)257);   // (x * 257) >> 16 ≈ x / 255

                fixed (byte* d = dst) // ptr to dst and source bufs
                fixed (byte* s = src)
                {
                    var i = 0;
                    for (; i + 32 <= total; i += 32)
                    {
                        var dVec = Avx.LoadDquVector256(d + i); // load 32 bytes from dst as a vector
                        var sVec = Avx.LoadDquVector256(s + i); // load 32 bytes from src as a vector

                        // unpack the vectors into low and high 16-bit halves
                        var dLo = Avx2.UnpackLow(dVec, Vector256<byte>.Zero).AsUInt16();
                        var dHi = Avx2.UnpackHigh(dVec, Vector256<byte>.Zero).AsUInt16();
                        var sLo = Avx2.UnpackLow(sVec, Vector256<byte>.Zero).AsUInt16();
                        var sHi = Avx2.UnpackHigh(sVec, Vector256<byte>.Zero).AsUInt16();

                        // SIMD multiply and add to compute the blended sum
                        var sumLo = Avx2.Add(Avx2.MultiplyLow(dLo, invV), Avx2.MultiplyLow(sLo, alphaV));
                        var sumHi = Avx2.Add(Avx2.MultiplyLow(dHi, invV), Avx2.MultiplyLow(sHi, alphaV));

                        var outLo = Avx2.MultiplyHigh(sumLo, magicV);   // /255
                        var outHi = Avx2.MultiplyHigh(sumHi, magicV);

                        var packed = Avx2.PackUnsignedSaturate(outLo.AsInt16(), outHi.AsInt16());
                        var reordered = Avx2.Permute4x64(packed.AsUInt64(), 0b11_01_10_00).AsByte();

                        // store the blended sum back to the destination
                        Avx.Store(d + i, reordered);
                    }

                    // for each remaining element, fall back to scalar multiplication if no SIMD support
                    for (; i < total; i++)
                        d[i] = (byte)((d[i] * inv + s[i] * alpha) / 255);
                }
                return;
            }

            var invScalar = 255 - alpha;
            for (var i = 0; i < total; i++)
                dst[i] = (byte)((dst[i] * invScalar + src[i] * alpha) / 255);
        }
    }
}
