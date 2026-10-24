using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FFmpeg.AutoGen;
using Fig.Core.Audio;
using Fig.Core.Timeline;
using ProjectModel = Fig.Core.Project.Project;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Media
{
    public partial class MediaService
    {
        /// <summary>
        /// Renders a full timeline (all tracks, effects, transitions, fades, speed, and mixed
        /// audio) to an MP4 (H.264 + AAC). Frame composition reuses the same pipeline as the
        /// preview (persistent sequential decoders, effect stack, transition blender, and
        /// painters-algorithm compositor), so the export matches what the preview shows.
        /// Each frame is described as a <see cref="RenderScene"/> and drawn by <paramref name="renderer"/>
        /// (the CPU kernels when none is given; the app passes the GPU renderer).
        /// </summary>
        public unsafe void RenderTimeline(ProjectModel project, TimelineModel timeline,
            string outputPath, int width, int height, int crf = 23, Action<double>? progress = null,
            double? rangeStartSec = null, double? rangeEndSec = null, ISceneRenderer? renderer = null,
            ILayerRasterizer? layers = null, VideoEncoderChoice encoder = VideoEncoderChoice.Automatic,
            Action<string>? encoderUsed = null, ExportTurn turn = ExportTurn.None)
        {
            if (timeline.Tracks.Count == 0)
                throw new InvalidOperationException("Timeline has no tracks");
            var end = TimelineEndSec(timeline);
            if (end <= 0)
                throw new InvalidOperationException("Timeline is empty");
            // optional sub-range (e.g. one marked section); the output always starts at 0
            var start = Math.Clamp(rangeStartSec ?? 0, 0, end);
            end = Math.Clamp(rangeEndSec ?? end, start, end);
            if (end - start <= 0)
                throw new InvalidOperationException("The selected range is empty");
            var length = end - start;
            if (timeline.Rate.Fps <= 0)
                throw new InvalidOperationException("Timeline has no frame rate");

            width = Math.Max(2, width & ~1);
            height = Math.Max(2, height & ~1);
            // the picture is composed as edited (width × height); turned, the file is the other way up
            var (outW, outH) = turn == ExportTurn.None ? (width, height) : (height, width);
            // frame f is at start + f x Den / Num: the timeline's exact rate (29.97 is 30000/1001, not 30)
            var rate = timeline.Rate;

            var sources = new Dictionary<string, IVideoFrameSource>();
            try
            {
                AVFormatContext* outCtx = null;
                AVCodecContext* vEnc = null;   // owned by opened
                OpenedEncoder? opened = null;
                AVCodecContext* aEnc = null;
                AVStream* vStream = null;
                AVStream* aStream = null;
                AVFrame* bgra = null;
                AVFrame* yuv = null;
                SwsContext* sws = null;
                try
                {
                    FfmpegOpen.Check(ffmpeg.avformat_alloc_output_context2(&outCtx, null, null, outputPath),
                        "avformat_alloc_output_context2");
                    if (outCtx == null)
                        throw new InvalidOperationException("Could not create output context");

                    // ---- video stream: H.264 on the GPU when there is one, else libx264, at the timeline's exact rate ----
                    opened = VideoEncoders.Open(encoder, outW, outH, timeline.Rate, crf,
                        (outCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0);
                    encoderUsed?.Invoke(opened.Label);
                    vEnc = opened.Context;
                    vStream = ffmpeg.avformat_new_stream(outCtx, opened.Codec);
                    FfmpegOpen.Check(ffmpeg.avcodec_parameters_from_context(vStream->codecpar, vEnc), "avcodec_parameters_from_context (video)");
                    vStream->time_base = vEnc->time_base;

                    // ---- audio stream: AAC @ 48 kHz stereo ----
                    var acodec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
                    if (acodec == null)
                        throw new InvalidOperationException("AAC encoder not found");
                    aEnc = ffmpeg.avcodec_alloc_context3(acodec);
                    aEnc->sample_rate = AudioMixer.SampleRate;
                    aEnc->bit_rate = 192000;
                    AVChannelLayout stereo = default;
                    ffmpeg.av_channel_layout_default(&stereo, 2);
                    aEnc->ch_layout = stereo;
                    aEnc->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
                    aEnc->time_base = new AVRational { num = 1, den = AudioMixer.SampleRate };
                    FfmpegOpen.Check(ffmpeg.avcodec_open2(aEnc, acodec, null), "avcodec_open2 (audio)");
                    aStream = ffmpeg.avformat_new_stream(outCtx, acodec);
                    FfmpegOpen.Check(ffmpeg.avcodec_parameters_from_context(aStream->codecpar, aEnc), "avcodec_parameters_from_context (audio)");
                    aStream->time_base = aEnc->time_base;
                    ffmpeg.av_channel_layout_uninit(&stereo);

                    FfmpegOpen.Check(ffmpeg.avio_open(&outCtx->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
                    FfmpegOpen.Check(ffmpeg.avformat_write_header(outCtx, null), "avformat_write_header");

                    // ---- scratch frames: compose to BGRA, convert to YUV420 ----
                    bgra = ffmpeg.av_frame_alloc();
                    bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    bgra->width = outW;
                    bgra->height = outH;
                    FfmpegOpen.Check(ffmpeg.av_frame_get_buffer(bgra, 32), "av_frame_get_buffer (bgra)");

                    yuv = ffmpeg.av_frame_alloc();
                    yuv->format = (int)opened.InputFormat;
                    yuv->width = outW;
                    yuv->height = outH;
                    FfmpegOpen.Check(ffmpeg.av_frame_get_buffer(yuv, 32), "av_frame_get_buffer (yuv)");

                    sws = ffmpeg.sws_getContext(outW, outH, AVPixelFormat.AV_PIX_FMT_BGRA,
                        outW, outH, opened.InputFormat, SWS_BILINEAR, null, null, null);
                    if (sws == null)
                        throw new InvalidOperationException("sws_getContext failed");

                    // ---- video frames: describe each as a scene, let the renderer draw it, encode in order ----
                    renderer ??= new CpuSceneRenderer();
                    var depth = Math.Max(1, renderer.MaxInFlight);
                    var totalFrames = Math.Max(1, (long)Math.Round(length * rate.Num / rate.Den));
                    var inFlight = new Queue<(long Frame, double Time, byte[] Canvas, Task Done)>();
                    var spare = new Stack<byte[]>();
                    for (var f = 0L; f < totalFrames; f++)
                    {
                        var t = start + f * (double)rate.Den / rate.Num;
                        var scene = BuildScene(project, timeline, sources, t, width, height, layers);
                        var canvas = spare.Count > 0 ? spare.Pop() : new byte[width * height * 4];
                        inFlight.Enqueue((f, t, canvas, renderer.RenderAsync(scene, canvas)));
                        while (inFlight.Count >= depth)
                        {
                            var item = inFlight.Dequeue();
                            FinishFrame(item.Done, item.Canvas, item.Frame, bgra, yuv, sws, opened, outCtx, vStream, width, height, turn);
                            spare.Push(item.Canvas);
                            progress?.Invoke(Math.Min(1, (item.Time - start) / length));
                        }
                    }
                    while (inFlight.Count > 0)
                    {
                        var item = inFlight.Dequeue();
                        FinishFrame(item.Done, item.Canvas, item.Frame, bgra, yuv, sws, opened, outCtx, vStream, width, height, turn);
                        spare.Push(item.Canvas);
                        progress?.Invoke(Math.Min(1, (item.Time - start) / length));
                    }
                    EncodeFrame(vEnc, outCtx, vStream, null, totalFrames);

                    // ---- audio: mix the whole timeline in 1024-frame chunks ----
                    var mixer = new AudioMixer(this, id => project.Media.FirstOrDefault(m => m.Id == id));
                    var aPts = 0L;
                    const int chunkFrames = 1024;
                    var pos = start;
                    while (pos < end - 1e-6)
                    {
                        var dur = Math.Min(chunkFrames / (double)AudioMixer.SampleRate, end - pos);
                        var floats = mixer.Mix(timeline, pos, dur);
                        var frames = floats.Length / 2;
                        if (frames > 0)
                            EncodeAudioFrames(aEnc, outCtx, aStream, floats, frames, ref aPts);
                        pos += dur;
                    }
                    ffmpeg.avcodec_send_frame(aEnc, null);
                    DrainAudioPackets(aEnc, outCtx, aStream);

                    ffmpeg.av_write_trailer(outCtx);
                }
                finally
                {
                    if (sws != null) ffmpeg.sws_freeContext(sws);
                    if (yuv != null) ffmpeg.av_frame_free(&yuv);
                    if (bgra != null) ffmpeg.av_frame_free(&bgra);
                    if (aEnc != null) ffmpeg.avcodec_free_context(&aEnc);
                    opened?.Dispose();
                    if (outCtx != null) ffmpeg.avformat_free_context(outCtx);
                }
            }
            finally
            {
                foreach (var source in sources.Values)
                    source.Dispose();
            }
        }

        private unsafe void EncodeAudioFrames(AVCodecContext* aEnc, AVFormatContext* outCtx, AVStream* aStream,
            float[] interleaved, int frames, ref long pts)
        {
            AVFrame* frame = ffmpeg.av_frame_alloc();
            frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            frame->nb_samples = frames;
            AVChannelLayout stereo = default;
            ffmpeg.av_channel_layout_default(&stereo, 2);
            frame->ch_layout = stereo;
            FfmpegOpen.Check(ffmpeg.av_frame_get_buffer(frame, 32), "av_frame_get_buffer (audio)");
            ffmpeg.av_channel_layout_uninit(&stereo);

            for (var c = 0; c < 2; c++)
            {
                var dst = (float*)frame->extended_data[c];
                for (var i = 0; i < frames; i++)
                    dst[i] = interleaved[i * 2 + c];
            }

            frame->pts = pts;
            pts += frames;
            ffmpeg.avcodec_send_frame(aEnc, frame);
            ffmpeg.av_frame_free(&frame);
            DrainAudioPackets(aEnc, outCtx, aStream);
        }

        private unsafe void DrainAudioPackets(AVCodecContext* aEnc, AVFormatContext* outCtx, AVStream* aStream)
        {
            AVPacket* pkt = ffmpeg.av_packet_alloc();
            try
            {
                while (ffmpeg.avcodec_receive_packet(aEnc, pkt) == 0)
                {
                    pkt->stream_index = aStream->index;
                    ffmpeg.av_packet_rescale_ts(pkt, aEnc->time_base, aStream->time_base);
                    ffmpeg.av_interleaved_write_frame(outCtx, pkt);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&pkt);
            }
        }

        /// <summary>Waits for a frame to be drawn, then copies it into the encoder's frame, converts to YUV and encodes it.</summary>
        /// <summary>
        /// Copies a composed picture (width × height) into a frame of the other shape (height × width), turned a
        /// quarter: turned right, the picture's top lies along the frame's right side; turned left, along its left.
        /// </summary>
        private static unsafe void Turn(byte* source, int width, int height, byte* frame, int frameStride, ExportTurn turn)
        {
            var src = (uint*)source;
            var rowStride = frameStride / 4;
            var dst = (uint*)frame;
            System.Threading.Tasks.Parallel.For(0, height, y =>
            {
                for (var x = 0; x < width; x++)
                {
                    var (dx, dy) = turn == ExportTurn.Right ? (height - 1 - y, x) : (y, width - 1 - x);
                    dst[dy * rowStride + dx] = src[y * width + x];
                }
            });
        }

        private unsafe void FinishFrame(Task drawn, byte[] canvas, long index, AVFrame* bgra, AVFrame* yuv, SwsContext* sws,
            OpenedEncoder encoder, AVFormatContext* outCtx, AVStream* vStream, int width, int height, ExportTurn turn)
        {
            var vEnc = encoder.Context;
            drawn.GetAwaiter().GetResult();
            var srcStride = width * 4;
            fixed (byte* pCanvas = canvas)
            {
                var dstStride = bgra->linesize[0];
                if (turn == ExportTurn.None)
                {
                    for (var y = 0; y < height; y++)
                        Buffer.MemoryCopy(
                            pCanvas + y * srcStride,
                            (byte*)bgra->data[0] + y * dstStride,
                            srcStride, srcStride);
                }
                else
                    Turn(pCanvas, width, height, (byte*)bgra->data[0], dstStride, turn);
            }

            ffmpeg.sws_scale(sws, bgra->data, bgra->linesize, 0, bgra->height, yuv->data, yuv->linesize);
            yuv->pts = index;
            if (encoder.HwFrames == null)
            {
                EncodeFrame(vEnc, outCtx, vStream, yuv, index);
                return;
            }
            // VA-API encodes GPU frames: upload this one into the encoder's pool first
            var gpu = ffmpeg.av_frame_alloc();
            try
            {
                FfmpegOpen.Check(ffmpeg.av_hwframe_get_buffer(encoder.HwFrames, gpu, 0), "av_hwframe_get_buffer");
                FfmpegOpen.Check(ffmpeg.av_hwframe_transfer_data(gpu, yuv, 0), "av_hwframe_transfer_data");
                EncodeFrame(vEnc, outCtx, vStream, gpu, index);
            }
            finally
            {
                ffmpeg.av_frame_free(&gpu);
            }
        }

        /// <summary>
        /// Decodes what is on screen at a timeline time and describes how to draw it: the layers bottom to
        /// top, each with its resolved effects, crop, transition and opacity. Nothing is drawn here.
        /// </summary>
        private RenderScene BuildScene(ProjectModel project, TimelineModel timeline,
            Dictionary<string, IVideoFrameSource> sources, double t, int width, int height, ILayerRasterizer? rasterizer)
        {
            var layers = new List<RenderLayer>();
            // paint from the lowest track up: a track higher on the timeline covers the ones below (matches the preview)
            foreach (var (_, track) in TrackStacking.BottomUp(timeline))
            {

                var trackEffects = EffectPipeline.ResolveStack(track.Effects, t);
                var activeTx = TransitionResolver.FindActive(track, t);
                if (activeTx is not null
                    && TransitionCatalog.Resolve(activeTx.TypeId) is not null
                    && TryTransitionFrame(project, timeline, sources, activeTx.Outgoing, t, width, height, rasterizer, out var outFrame, out var outLocal)
                    && TryTransitionFrame(project, timeline, sources, activeTx.Incoming, t, width, height, rasterizer, out var inFrame, out var inLocal))
                {
                    var outClip = activeTx.Outgoing;
                    var inClip = activeTx.Incoming;
                    var outOpacity = ClipFade.EffectiveOpacity(outClip, Math.Clamp(outLocal, 0, outClip.DurSec));
                    var inOpacity = ClipFade.EffectiveOpacity(inClip, Math.Clamp(inLocal, 0, inClip.DurSec));
                    layers.Add(new RenderLayer
                    {
                        Source = SourceFor(outClip, outFrame!, outLocal),
                        Incoming = SourceFor(inClip, inFrame!, inLocal),
                        TransitionTypeId = activeTx.TypeId,
                        Progress = activeTx.Progress01,
                        TransitionParams = activeTx.Params,
                        Effects = trackEffects,
                        Opacity = outOpacity + (inOpacity - outOpacity) * activeTx.Progress01,
                    });
                    continue;
                }

                var trackLayers = new List<RenderLayer>();
                foreach (var clip in track.Clips)
                {
                    if (!clip.Enabled || t < clip.StartSec || t >= clip.StartSec + clip.DurSec)
                        continue;
                    var localT = t - clip.StartSec;
                    DecodedFrame? frame;
                    if (clip is ILayerClip)
                        frame = rasterizer?.Rasterize(clip, width, height, localT);   // drawn, with transparency
                    else if (clip is VideoClip vc && TryDecodeClip(project, timeline, sources, vc, t, width, height, out var decoded, out _))
                        frame = decoded;
                    else
                        frame = null;
                    if (frame is null)
                        continue;
                    trackLayers.Add(new RenderLayer
                    {
                        Source = new RenderSource
                        {
                            Frame = frame,
                            Effects = EffectPipeline.ResolveStack(clip.Effects, localT),
                            Crop = CropAt(clip, localT),
                            HasAlpha = clip is ILayerClip,
                        },
                        Effects = trackEffects,
                        Opacity = ClipFade.EffectiveOpacity(clip, localT),
                    });
                }
                // clips earlier in a track sit above later ones
                trackLayers.Reverse();
                layers.AddRange(trackLayers);
            }
            return new RenderScene { Width = width, Height = height, Layers = layers };
        }

        private static RenderSource SourceFor(Clip clip, DecodedFrame frame, double localT)
            => new()
            {
                Frame = frame,
                Effects = EffectPipeline.ResolveStack(clip.Effects, localT),
                Crop = CropAt(clip, localT),
                HasAlpha = clip is ILayerClip,
            };

        /// <summary>
        /// One side of a transition: a video's decoded frame, or a still or title drawn by <paramref name="rasterizer"/> (held
        /// at its nearest end while the transition runs past it).
        /// </summary>
        private bool TryTransitionFrame(ProjectModel project, TimelineModel timeline,
            Dictionary<string, IVideoFrameSource> sources, Clip clip, double t, int width, int height,
            ILayerRasterizer? rasterizer, out DecodedFrame? frame, out double localT)
        {
            frame = null;
            localT = t - clip.StartSec;
            if (clip is ILayerClip)
                frame = rasterizer?.Rasterize(clip, width, height, Math.Clamp(localT, 0, Math.Max(0, clip.DurSec - 1e-4)));
            else if (clip is VideoClip video)
                TryDecodeClip(project, timeline, sources, video, t, width, height, out frame, out localT);
            return frame is not null;
        }

        /// <summary>The clip's crop at a clip-relative time (automation sampled); none for clips that can't be cropped.</summary>
        public static SceneCrop CropAt(Clip clip, double localT)
        {
            localT = Math.Clamp(localT, 0, Math.Max(0, clip.DurSec - 1e-4));
            var (l, top, r, bottom) = ClipAutomation.SampleCrop(clip, localT);
            return new SceneCrop(l, top, r, bottom);
        }

        private bool TryDecodeClip(ProjectModel project, TimelineModel timeline,
            Dictionary<string, IVideoFrameSource> sources, VideoClip clip, double timelineSec,
            int width, int height, out DecodedFrame? frame, out double localT)
        {
            frame = null;
            localT = 0;
            var asset = project.Media.FirstOrDefault(m => m.Id == clip.SourceId);
            if (asset is null || string.IsNullOrEmpty(asset.Url) || asset.Offline)
                return false;

            localT = timelineSec - clip.StartSec;
            var srcTime = ClipTime.SourceAt(clip, localT);
            var sourceStart = asset.SourceStartSec;
            var sourceEnd = sourceStart + Math.Max(0, asset.DurationSec - 1e-4);
            srcTime = Math.Clamp(srcTime, sourceStart, Math.Max(sourceStart, sourceEnd));

            var path = asset.Url;   // exports always read the original, never the preview proxy
            var decoderKey = $"{clip.Id}|{path}";
            if (!sources.TryGetValue(decoderKey, out var source))
            {
                source = OpenVideoSource(path, width, height);
                sources[decoderKey] = source;
            }

            // a blended frame sits up to a frame before the one decoded last: only seek back further than that
            var slack = clip.Smooths ? 0.1 : 0.05;
            if (source.LastPresentedTimeSec < 0 || srcTime < source.LastPresentedTimeSec - slack)
                source.Seek(srcTime);
            frame = clip.Smooths
                ? source.DecodeSmoothed(srcTime, ClipTime.SpanAt(clip, localT, 1 / Math.Max(1, timeline.Rate.Fps)),
                    clip.BlendFrames, clip.MotionBlur, PreviewDecodeMode.Playback)
                : source.DecodeForward(srcTime, PreviewDecodeMode.Playback);
            return frame is not null;
        }

        private static double TimelineEndSec(TimelineModel timeline)
        {
            double end = 0;
            foreach (var track in timeline.Tracks)
                foreach (var clip in track.Clips)
                {
                    if (!clip.Enabled)
                        continue;
                    end = Math.Max(end, clip.StartSec + clip.DurSec);
                }
            return end;
        }
    }
}
