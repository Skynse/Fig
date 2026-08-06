using System;
using System.Collections.Generic;
using System.Linq;
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
        /// </summary>
        public unsafe void RenderTimeline(ProjectModel project, TimelineModel timeline,
            string outputPath, int width, int height, int crf = 23, Action<double>? progress = null)
        {
            if (timeline.Tracks.Count == 0)
                throw new InvalidOperationException("Timeline has no tracks");
            var end = TimelineEndSec(timeline);
            if (end <= 0)
                throw new InvalidOperationException("Timeline is empty");
            if (timeline.Rate.Fps <= 0)
                throw new InvalidOperationException("Timeline has no frame rate");

            width = Math.Max(2, width & ~1);
            height = Math.Max(2, height & ~1);
            var fps = Math.Max(1, (int)Math.Round(timeline.Rate.Fps));

            var sources = new Dictionary<string, IVideoFrameSource>();
            try
            {
                AVFormatContext* outCtx = null;
                AVCodecContext* vEnc = null;
                AVCodecContext* aEnc = null;
                AVStream* vStream = null;
                AVStream* aStream = null;
                AVFrame* bgra = null;
                AVFrame* yuv = null;
                SwsContext* sws = null;
                try
                {
                    ThrowIfError(ffmpeg.avformat_alloc_output_context2(&outCtx, null, null, outputPath),
                        "avformat_alloc_output_context2");
                    if (outCtx == null)
                        throw new InvalidOperationException("Could not create output context");

                    // ---- video stream: libx264 @ timeline rate ----
                    var vcodec = ffmpeg.avcodec_find_encoder_by_name("libx264");
                    if (vcodec == null)
                        throw new InvalidOperationException("libx264 encoder not found");
                    vEnc = ffmpeg.avcodec_alloc_context3(vcodec);
                    vEnc->width = width;
                    vEnc->height = height;
                    vEnc->time_base = new AVRational { num = 1, den = fps };
                    vEnc->framerate = new AVRational { num = fps, den = 1 };
                    vEnc->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                    vEnc->gop_size = fps * 2;
                    vEnc->max_b_frames = 0;
                    var crfStr = Math.Clamp(crf, 0, 51).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    ffmpeg.av_opt_set(vEnc->priv_data, "crf", crfStr, 0);
                    ThrowIfError(ffmpeg.avcodec_open2(vEnc, vcodec, null), "avcodec_open2 (video)");
                    vStream = ffmpeg.avformat_new_stream(outCtx, vcodec);
                    ThrowIfError(ffmpeg.avcodec_parameters_from_context(vStream->codecpar, vEnc), "avcodec_parameters_from_context (video)");
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
                    ThrowIfError(ffmpeg.avcodec_open2(aEnc, acodec, null), "avcodec_open2 (audio)");
                    aStream = ffmpeg.avformat_new_stream(outCtx, acodec);
                    ThrowIfError(ffmpeg.avcodec_parameters_from_context(aStream->codecpar, aEnc), "avcodec_parameters_from_context (audio)");
                    aStream->time_base = aEnc->time_base;
                    ffmpeg.av_channel_layout_uninit(&stereo);

                    ThrowIfError(ffmpeg.avio_open(&outCtx->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
                    ThrowIfError(ffmpeg.avformat_write_header(outCtx, null), "avformat_write_header");

                    // ---- scratch frames: compose to BGRA, convert to YUV420 ----
                    bgra = ffmpeg.av_frame_alloc();
                    bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    bgra->width = width;
                    bgra->height = height;
                    ThrowIfError(ffmpeg.av_frame_get_buffer(bgra, 32), "av_frame_get_buffer (bgra)");

                    yuv = ffmpeg.av_frame_alloc();
                    yuv->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                    yuv->width = width;
                    yuv->height = height;
                    ThrowIfError(ffmpeg.av_frame_get_buffer(yuv, 32), "av_frame_get_buffer (yuv)");

                    sws = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                        width, height, AVPixelFormat.AV_PIX_FMT_YUV420P, SWS_BILINEAR, null, null, null);
                    if (sws == null)
                        throw new InvalidOperationException("sws_getContext failed");

                    // ---- video frames ----
                    var canvas = new byte[width * height * 4];
                    var srcStride = width * 4;
                    var totalFrames = (long)Math.Round(end * fps);
                    for (var f = 0L; f < totalFrames; f++)
                    {
                        var t = f / (double)fps;
                        ComposeCanvas(project, timeline, sources, t, width, height, canvas);

                        fixed (byte* pCanvas = canvas)
                        {
                            var dstStride = bgra->linesize[0];
                            for (var y = 0; y < height; y++)
                                Buffer.MemoryCopy(
                                    pCanvas + y * srcStride,
                                    (byte*)bgra->data[0] + y * dstStride,
                                    srcStride, srcStride);
                        }

                        ffmpeg.sws_scale(sws, bgra->data, bgra->linesize, 0, height, yuv->data, yuv->linesize);
                        yuv->pts = f;
                        EncodeFrame(vEnc, outCtx, vStream, yuv, f);
                        progress?.Invoke(Math.Min(1, t / end));
                    }
                    EncodeFrame(vEnc, outCtx, vStream, null, totalFrames);

                    // ---- audio: mix the whole timeline in 1024-frame chunks ----
                    var mixer = new AudioMixer(this, id => project.Media.FirstOrDefault(m => m.Id == id));
                    var aPts = 0L;
                    const int chunkFrames = 1024;
                    var pos = 0.0;
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
                    if (vEnc != null) ffmpeg.avcodec_free_context(&vEnc);
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
            ThrowIfError(ffmpeg.av_frame_get_buffer(frame, 32), "av_frame_get_buffer (audio)");
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

        /// <summary>Composites the video picture at a timeline time into a full-canvas BGRA buffer.</summary>
        private void ComposeCanvas(ProjectModel project, TimelineModel timeline,
            Dictionary<string, IVideoFrameSource> sources, double t, int width, int height, byte[] canvas)
        {
            Array.Clear(canvas, 0, canvas.Length);
            for (var i = 3; i < canvas.Length; i += 4)
                canvas[i] = 255;

            var rented = new List<byte[]>();
            byte[]? cropScratch = null;
            try
            {
                var activeTx = TransitionResolver.FindActive(timeline, t);
                if (activeTx is not null
                    && activeTx.Outgoing is VideoClip outVc
                    && activeTx.Incoming is VideoClip inVc
                    && TransitionRegistry.Resolve(activeTx.TypeId) is { } blender
                    && TryDecodeClip(project, timeline, sources, outVc, t, width, height, out var outFrame, out var outLocal)
                    && TryDecodeClip(project, timeline, sources, inVc, t, width, height, out var inFrame, out var inLocal))
                {
                    outFrame = EffectPipeline.ApplyStack(outFrame!, outVc.Effects, outLocal, rented);
                    inFrame = EffectPipeline.ApplyStack(inFrame!, inVc.Effects, inLocal, rented);
                    var blended = blender.Blend(outFrame, inFrame, activeTx.Progress01, activeTx.Params);
                    try
                    {
                        FrameCompositor.ComposeInto(
                            new[] { new CompositeLayer { Frame = blended, Opacity = 1 } },
                            width, height, canvas, ref cropScratch);
                    }
                    finally
                    {
                        FramePool.Return(blended.Pixels);
                    }
                    return;
                }

                // painters algorithm: the last track is the top layer (matches the preview)
                var composites = new List<CompositeLayer>();
                for (var i = timeline.Tracks.Count - 1; i >= 0; i--)
                {
                    var track = timeline.Tracks[i];
                    if (track.Kind != TrackKind.Video || !track.Visible)
                        continue;

                    foreach (var clip in track.Clips)
                    {
                        if (clip is not VideoClip vc || !vc.Enabled)
                            continue;
                        if (t < clip.StartSec || t >= clip.StartSec + clip.DurSec)
                            continue;

                        if (!TryDecodeClip(project, timeline, sources, vc, t, width, height, out var frame, out var localT))
                            continue;
                        frame = EffectPipeline.ApplyStack(frame!, vc.Effects, localT, rented);
                        composites.Add(new CompositeLayer
                        {
                            Frame = frame,
                            Opacity = ClipFade.EffectiveOpacity(vc, localT),
                            Crop = null,
                        });
                    }
                }
                FrameCompositor.ComposeInto(composites, width, height, canvas, ref cropScratch);
            }
            finally
            {
                foreach (var buf in rented)
                    FramePool.Return(buf);
            }
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

            localT = Math.Clamp(timelineSec - clip.StartSec, 0, Math.Max(0, clip.DurSec - 1e-4));
            var ratio = clip.SourceRate is { } r ? r.Fps / timeline.Rate.Fps : 1.0;
            var srcTime = clip.SrcInSec + localT * clip.Speed * ratio;

            var path = asset.PlaybackVideoPath;
            if (!sources.TryGetValue(path, out var source))
            {
                source = OpenVideoSource(path, width, height);
                sources[path] = source;
            }

            if (source.LastPresentedTimeSec < 0 || srcTime < source.LastPresentedTimeSec - 0.05)
                source.Seek(srcTime);
            frame = source.DecodeForward(srcTime, PreviewDecodeMode.Playback);
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
