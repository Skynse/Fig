using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Fig.App.Views
{
    /// <summary>
    /// A video preview surface backed by a single reused <see cref="WriteableBitmap"/>.
    /// Frames are written in place with <see cref="Present"/> (locking the GPU surface and
    /// copying BGRA pixels), so playback never allocates a bitmap per frame. The surface
    /// scales the frame uniformly to fit, preserving aspect ratio.
    /// </summary>
    public class PreviewSurface : Control
    {
        private WriteableBitmap? _bitmap;
        private int _bitmapW;
        private int _bitmapH;

        public PreviewSurface()
        {
            ClipToBounds = true;
        }

        /// <summary>Writes a BGRA frame into the reused surface and repaints. Allocates a new surface only when the size changes.</summary>
        public void Present(int width, int height, byte[] bgra)
        {
            if (width <= 0 || height <= 0 || bgra.Length < width * height * 4)
                return;

            if (_bitmap is null || _bitmapW != width || _bitmapH != height)
            {
                _bitmap?.Dispose();
                _bitmap = new WriteableBitmap(
                    new PixelSize(width, height),
                    new Vector(96, 96),
                    PixelFormat.Bgra8888,
                    AlphaFormat.Opaque);
                _bitmapW = width;
                _bitmapH = height;
            }

            using (var fb = _bitmap.Lock())
            {
                // copy BGRA (bottom-up row order matches Skia's buffer layout)
                System.Runtime.InteropServices.Marshal.Copy(bgra, 0, fb.Address, width * height * 4);
            }

            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);
            if (_bitmap is null || Bounds.Width <= 0 || Bounds.Height <= 0)
                return;

            var scale = Math.Min(Bounds.Width / _bitmapW, Bounds.Height / _bitmapH);
            var w = _bitmapW * scale;
            var h = _bitmapH * scale;
            var x = (Bounds.Width - w) / 2;
            var y = (Bounds.Height - h) / 2;
            context.DrawImage(_bitmap, new Rect(x, y, w, h));
        }
    }
}
