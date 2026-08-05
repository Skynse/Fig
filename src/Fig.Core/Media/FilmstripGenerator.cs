using System;
using System.IO;
using FFmpeg.AutoGen;

namespace Fig.Core.Media
{
    public partial class MediaService
    {
        /// <summary>
        /// Builds a timeline filmstrip from <b>keyframes only</b> in one forward demux pass.
        /// Full-frame decode of long webm/AV1 files can take minutes; keyframes are typically
        /// 10–50× faster and look fine at timeline scale. Sparse-GOP media repeats the nearest
        /// keyframe across empty tiles rather than decoding every frame.
        /// </summary>
        public unsafe FilmstripInfo GenerateFilmstrip(string sourcePath, string outputPath, int tileHeight = 40)
        {
            AVFormatContext* inCtx = null;
            AVCodecContext* decCtx = null;
            AVFrame* frame = null;
            AVPacket* packet = null;
            SwsContext* sws = null;
            AVFrame* rgb = null;
            AVFrame* strip = null;
            AVCodecContext* encCtx = null;
            byte* stripBuf = null;

            try
            {
                var pIn = inCtx;
                ThrowIfError(ffmpeg.avformat_open_input(&pIn, sourcePath, null, null), "avformat_open_input");
                inCtx = pIn;
                ThrowIfError(ffmpeg.avformat_find_stream_info(inCtx, null), "avformat_find_stream_info");

                var vIdx = ffmpeg.av_find_best_stream(inCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                ThrowIfError(vIdx, "av_find_best_stream");
                var inStream = inCtx->streams[vIdx];
                var inCodecPar = inStream->codecpar;

                var dec = ffmpeg.avcodec_find_decoder(inCodecPar->codec_id);
                if (dec == null)
                    throw new InvalidOperationException("No decoder found");
                decCtx = ffmpeg.avcodec_alloc_context3(dec);
                ThrowIfError(ffmpeg.avcodec_parameters_to_context(decCtx, inCodecPar), "avcodec_parameters_to_context");
                decCtx->pkt_timebase = inStream->time_base;
                // ask the decoder to drop non-keyframes; we also filter packets below
                decCtx->skip_frame = AVDiscard.AVDISCARD_NONKEY;
                ThrowIfError(ffmpeg.avcodec_open2(decCtx, dec, null), "avcodec_open2");

                var duration = inCtx->duration > 0
                    ? inCtx->duration * ffmpeg.av_q2d(ffmpeg.av_get_time_base_q())
                    : inStream->duration * ffmpeg.av_q2d(inStream->time_base);
                if (duration <= 0)
                    duration = 1.0;

                var srcW = Math.Max(1, inCodecPar->width);
                var srcH = Math.Max(1, inCodecPar->height);

                // ~1 tile / 5s, clamped — enough for timeline scrub, cheap to build
                var frames = Math.Clamp((int)Math.Ceiling(duration / 5.0), 8, 24);
                var interval = duration / frames;
                // even tile width so the full strip stays YUV420-safe (even × N)
                var tileW = Math.Max(2, (int)Math.Round(tileHeight * srcW / (double)srcH) & ~1);
                if (tileW * frames > 2048)
                    tileW = Math.Max(2, (2048 / frames) & ~1);
                var width = tileW * frames;
                // YUV420 JPEG encode requires even dimensions — odd sizes smash the heap
                var height = Math.Max(2, tileHeight & ~1);

                rgb = ffmpeg.av_frame_alloc();
                rgb->format = (int)AVPixelFormat.AV_PIX_FMT_RGB24;
                rgb->width = tileW;
                rgb->height = height;
                ThrowIfError(ffmpeg.av_frame_get_buffer(rgb, 32), "av_frame_get_buffer");

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                var stripBytes = (long)width * height * 3;
                stripBuf = (byte*)ffmpeg.av_malloc((ulong)stripBytes);
                if (stripBuf == null)
                    throw new OutOfMemoryException("filmstrip buffer");
                NativeMemoryClear(stripBuf, stripBytes);

                var timeBase = ffmpeg.av_q2d(inStream->time_base);
                // start at t=0 so a sole keyframe at PTS 0 actually fills tile 0
                var nextTile = 0;
                var nextTileSec = 0.0;
                var filled = new bool[frames];
                var swsKey = (0, 0, AVPixelFormat.AV_PIX_FMT_NONE);

                while (nextTile < frames && ffmpeg.av_read_frame(inCtx, packet) >= 0)
                {
                    try
                    {
                        if (packet->stream_index != vIdx)
                            continue;
                        if ((packet->flags & ffmpeg.AV_PKT_FLAG_KEY) == 0)
                            continue;
                        if (ffmpeg.avcodec_send_packet(decCtx, packet) < 0)
                            continue;

                        while (nextTile < frames && ffmpeg.avcodec_receive_frame(decCtx, frame) == 0)
                            StampTiles(ref sws, ref swsKey, frame, rgb, stripBuf,
                                ref nextTile, ref nextTileSec, filled, frames, interval,
                                timeBase, width, height, tileW);
                    }
                    finally
                    {
                        ffmpeg.av_packet_unref(packet);
                    }
                }

                ffmpeg.avcodec_send_packet(decCtx, null);
                while (nextTile < frames && ffmpeg.avcodec_receive_frame(decCtx, frame) == 0)
                    StampTiles(ref sws, ref swsKey, frame, rgb, stripBuf,
                        ref nextTile, ref nextTileSec, filled, frames, interval,
                        timeBase, width, height, tileW);

                FillGaps(stripBuf, filled, frames, width, height, tileW);

                var any = false;
                for (var i = 0; i < frames; i++)
                {
                    if (filled[i]) { any = true; break; }
                }
                if (!any)
                    throw new InvalidOperationException("No video frames decoded for filmstrip");

                EncodeStripJpeg(stripBuf, width, height, outputPath, ref strip, ref encCtx);

                return new FilmstripInfo
                {
                    Path = outputPath,
                    FrameWidth = tileW,
                    FrameHeight = height,
                    FrameCount = frames,
                    FrameIntervalSec = interval,
                };
            }
            finally
            {
                if (stripBuf != null) ffmpeg.av_free(stripBuf);
                if (encCtx != null) ffmpeg.avcodec_free_context(&encCtx);
                if (rgb != null) ffmpeg.av_frame_free(&rgb);
                if (strip != null) ffmpeg.av_frame_free(&strip);
                if (sws != null) ffmpeg.sws_freeContext(sws);
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (packet != null) ffmpeg.av_packet_free(&packet);
                if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
                if (inCtx != null)
                {
                    var pIn = inCtx;
                    ffmpeg.avformat_close_input(&pIn);
                }
            }
        }

        private static unsafe void StampTiles(
            ref SwsContext* sws, ref (int W, int H, AVPixelFormat Fmt) swsKey,
            AVFrame* frame, AVFrame* rgb, byte* stripBuf,
            ref int nextTile, ref double nextTileSec, bool[] filled, int frames, double interval,
            double timeBase, int width, int height, int tileW)
        {
            EnsureSws(ref sws, ref swsKey, frame, tileW, height);
            if (sws == null)
                return;

            var pts = frame->best_effort_timestamp;
            var sec = pts >= 0 ? pts * timeBase : nextTileSec;
            while (nextTile < frames && sec + 1e-6 >= nextTileSec)
            {
                if (!filled[nextTile])
                {
                    CopyTile(sws, frame, rgb, stripBuf, width, height, tileW, nextTile);
                    filled[nextTile] = true;
                }
                nextTile++;
                nextTileSec = interval * nextTile;
            }
        }

        private static unsafe void FillGaps(byte* stripBuf, bool[] filled, int frames, int width, int height, int tileW)
        {
            var lastFilled = -1;
            for (var i = 0; i < frames; i++)
            {
                if (filled[i])
                {
                    lastFilled = i;
                    continue;
                }
                if (lastFilled < 0)
                    continue;
                CopyStripTile(stripBuf, width, height, tileW, lastFilled, i);
                filled[i] = true;
            }

            var firstFilled = -1;
            for (var i = 0; i < frames; i++)
            {
                if (filled[i]) { firstFilled = i; break; }
            }
            if (firstFilled <= 0)
                return;
            for (var i = 0; i < firstFilled; i++)
            {
                CopyStripTile(stripBuf, width, height, tileW, firstFilled, i);
                filled[i] = true;
            }
        }

        private static unsafe void CopyStripTile(byte* stripBuf, int width, int height, int tileW, int from, int to)
        {
            for (var y = 0; y < height; y++)
            {
                var dstRow = stripBuf + (long)(y * width + to * tileW) * 3;
                var srcRow = stripBuf + (long)(y * width + from * tileW) * 3;
                Buffer.MemoryCopy(srcRow, dstRow, tileW * 3, tileW * 3);
            }
        }

        private static unsafe void EnsureSws(
            ref SwsContext* sws, ref (int W, int H, AVPixelFormat Fmt) swsKey,
            AVFrame* frame, int tileW, int height)
        {
            // always use the decoded frame's format/size — codecpar/decCtx are wrong for webm/AV1
            // until the first frame, and a mismatched sws_scale overruns the destination buffer
            var fmt = (AVPixelFormat)frame->format;
            var fw = frame->width;
            var fh = frame->height;
            if (fmt == AVPixelFormat.AV_PIX_FMT_NONE || fw <= 0 || fh <= 0)
                return;
            if (sws != null && swsKey.W == fw && swsKey.H == fh && swsKey.Fmt == fmt)
                return;
            if (sws != null)
                ffmpeg.sws_freeContext(sws);
            sws = ffmpeg.sws_getContext(fw, fh, fmt, tileW, height,
                AVPixelFormat.AV_PIX_FMT_RGB24, SWS_BILINEAR, null, null, null);
            swsKey = (fw, fh, fmt);
        }

        private static unsafe void CopyTile(
            SwsContext* sws, AVFrame* frame, AVFrame* rgb, byte* stripBuf,
            int width, int height, int tileW, int index)
        {
            ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, frame->height, rgb->data, rgb->linesize);
            var copyBytes = Math.Min(tileW * 3, rgb->linesize[0]);
            for (var y = 0; y < height; y++)
            {
                var dstRow = stripBuf + (long)(y * width + index * tileW) * 3;
                var srcRow = rgb->data[0] + y * rgb->linesize[0];
                Buffer.MemoryCopy(srcRow, dstRow, tileW * 3, copyBytes);
            }
        }

        private static unsafe void NativeMemoryClear(byte* ptr, long bytes)
        {
            for (long i = 0; i < bytes; i++)
                ptr[i] = 0;
        }

        private static unsafe void EncodeStripJpeg(
            byte* stripBuf, int width, int height, string outputPath,
            ref AVFrame* strip, ref AVCodecContext* encCtx)
        {
            // YUV420 planes require even w/h — callers should already align, belt-and-suspenders here
            width &= ~1;
            height &= ~1;
            if (width < 2 || height < 2)
                throw new InvalidOperationException("Filmstrip too small to encode");

            strip = ffmpeg.av_frame_alloc();
            strip->format = (int)AVPixelFormat.AV_PIX_FMT_RGB24;
            strip->width = width;
            strip->height = height;
            ThrowIfError(ffmpeg.av_frame_get_buffer(strip, 32), "av_frame_get_buffer(strip)");

            var stripLinesize = strip->linesize[0];
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(
                    stripBuf + y * (long)width * 3,
                    strip->data[0] + y * stripLinesize,
                    stripLinesize, width * 3);

            var enc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
            if (enc == null)
                throw new InvalidOperationException("No mjpeg encoder found");
            encCtx = ffmpeg.avcodec_alloc_context3(enc);
            encCtx->width = width;
            encCtx->height = height;
            encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            encCtx->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            encCtx->time_base = new AVRational { num = 1, den = 30 };
            encCtx->qmin = 5;
            encCtx->qmax = 8;
            ThrowIfError(ffmpeg.avcodec_open2(encCtx, enc, null), "avcodec_open2");

            var sws2 = ffmpeg.sws_getContext(width, height, AVPixelFormat.AV_PIX_FMT_RGB24,
                width, height, AVPixelFormat.AV_PIX_FMT_YUV420P, SWS_BILINEAR, null, null, null);
            if (sws2 == null)
                throw new InvalidOperationException("sws_getContext failed for filmstrip encode");

            AVFrame* yuv = null;
            AVPacket* outPkt = null;
            try
            {
                yuv = ffmpeg.av_frame_alloc();
                yuv->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                yuv->color_range = AVColorRange.AVCOL_RANGE_JPEG;
                yuv->width = width;
                yuv->height = height;
                ThrowIfError(ffmpeg.av_frame_get_buffer(yuv, 32), "av_frame_get_buffer(yuv)");
                ffmpeg.sws_scale(sws2, strip->data, strip->linesize, 0, height, yuv->data, yuv->linesize);
                yuv->pts = 0;
                ThrowIfError(ffmpeg.avcodec_send_frame(encCtx, yuv), "avcodec_send_frame");

                var dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                outPkt = ffmpeg.av_packet_alloc();
                while (ffmpeg.avcodec_receive_packet(encCtx, outPkt) == 0)
                {
                    try
                    {
                        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                        fs.Write(new ReadOnlySpan<byte>(outPkt->data, outPkt->size));
                    }
                    finally
                    {
                        ffmpeg.av_packet_unref(outPkt);
                    }
                }
            }
            finally
            {
                if (outPkt != null) ffmpeg.av_packet_free(&outPkt);
                if (yuv != null) ffmpeg.av_frame_free(&yuv);
                ffmpeg.sws_freeContext(sws2);
            }
        }
    }
}
