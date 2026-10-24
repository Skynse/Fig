using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Fig.Core.Media
{
    /// <summary>
    /// Draws a scene with the CPU kernels: the effect processors, transition blenders and the SIMD compositor.
    /// It is the reference the GPU renderer is tested against, and the fallback when there is no GPU.
    /// </summary>
    public sealed class CpuSceneRenderer : ISceneRenderer
    {
        public int MaxInFlight => 1;

        public Task RenderAsync(RenderScene scene, byte[] canvas)
        {
            Render(scene, canvas);
            return Task.CompletedTask;
        }

        public void Render(RenderScene scene, byte[] canvas)
        {
            var width = scene.Width;
            var height = scene.Height;
            var rented = new List<byte[]>();
            var seen = new HashSet<byte[]>();
            byte[]? cropScratch = null;
            try
            {
                // FrameCompositor takes the topmost layer first; a scene lists the bottom layer first
                var composites = new List<CompositeLayer>(scene.Layers.Count);
                for (var i = scene.Layers.Count - 1; i >= 0; i--)
                {
                    var layer = scene.Layers[i];
                    if (layer.Incoming is { } incoming && layer.TransitionTypeId is { } typeId
                        && TransitionCatalog.Resolve(typeId) is { } blender)
                    {
                        var outFrame = Prepare(layer.Source, width, height, seen, rented);
                        var inFrame = Prepare(incoming, width, height, seen, rented);
                        // a still has real transparency; video beside it must count as fully opaque in the blend
                        var withAlpha = layer.Source.HasAlpha || incoming.HasAlpha;
                        if (withAlpha && !layer.Source.HasAlpha)
                            outFrame.MakeOpaque();
                        if (withAlpha && !incoming.HasAlpha)
                            inFrame.MakeOpaque();
                        var blended = blender.Blend(outFrame, inFrame, layer.Progress, layer.TransitionParams
                            ?? new Dictionary<string, Fig.Core.Timeline.ParamValue>());
                        if (!ReferenceEquals(blended, outFrame) && !ReferenceEquals(blended, inFrame))
                            rented.Add(blended.Pixels);
                        blended = EffectPipeline.ApplyCalls(blended, layer.Effects, rented);
                        composites.Add(new CompositeLayer { Frame = blended, Opacity = layer.Opacity, Premultiplied = withAlpha });
                        continue;
                    }

                    var frame = FramePool.MakeDistinct(layer.Source.Frame, seen, rented);
                    frame = EffectPipeline.ApplyCalls(frame, layer.Source.Effects, rented);
                    RectI? crop;
                    if (layer.Effects.Count > 0 && !layer.Source.HasAlpha)
                    {
                        // track effects see the cropped picture, so the crop cannot wait for the compositor
                        frame = ApplyCrop(frame, layer.Source.Crop, width, height, rented);
                        frame = EffectPipeline.ApplyCalls(frame, layer.Effects, rented);
                        crop = null;
                    }
                    else
                    {
                        // a drawn layer keeps its transparency, so its track effects run on it directly
                        if (layer.Source.HasAlpha)
                            frame = EffectPipeline.ApplyCalls(frame, layer.Effects, rented);
                        crop = ToPixelCrop(layer.Source.Crop, width, height);
                    }
                    composites.Add(new CompositeLayer
                    {
                        Frame = frame, Opacity = layer.Opacity, Crop = crop, Premultiplied = layer.Source.HasAlpha,
                    });
                }
                FrameCompositor.ComposeInto(composites, width, height, canvas, ref cropScratch);
            }
            finally
            {
                foreach (var buffer in rented)
                    FramePool.Return(buffer);
            }
        }

        /// <summary>A source's picture after its own effects and crop (one side of a transition).</summary>
        private static DecodedFrame Prepare(RenderSource source, int width, int height, HashSet<byte[]> seen, List<byte[]> rented)
        {
            var frame = FramePool.MakeDistinct(source.Frame, seen, rented);
            frame = EffectPipeline.ApplyCalls(frame, source.Effects, rented);
            return ApplyCrop(frame, source.Crop, width, height, rented);
        }

        private static DecodedFrame ApplyCrop(DecodedFrame frame, SceneCrop crop, int width, int height, List<byte[]> rented)
        {
            if (ToPixelCrop(crop, width, height) is not { } rect)
                return frame;
            var pixels = FramePool.Rent(width * height * 4);
            byte[]? scratch = pixels;
            FrameCompositor.ComposeInto(
                new[] { new CompositeLayer { Frame = frame, Crop = rect, Opacity = 1 } },
                width, height, pixels, ref scratch);
            rented.Add(pixels);
            return new DecodedFrame { Width = width, Height = height, Pixels = pixels };
        }

        /// <summary>The crop in whole pixels, or null for none (also when it would leave nothing).</summary>
        public static RectI? ToPixelCrop(SceneCrop crop, int width, int height)
        {
            if (crop.IsNone || width <= 0 || height <= 0)
                return null;
            var x = (int)Math.Round(crop.Left * width);
            var y = (int)Math.Round(crop.Top * height);
            var w = (int)Math.Round((1 - crop.Left - crop.Right) * width);
            var h = (int)Math.Round((1 - crop.Top - crop.Bottom) * height);
            return w < 1 || h < 1 ? null : new RectI(x, y, w, h);
        }
    }
}
