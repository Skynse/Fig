using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Fig.Core.Timeline;
using FFmpeg.AutoGen;

namespace Fig.Core.Media
{
    public class MediaService : IMediaService
    {
        private const int SWS_BILINEAR = 2;

        public MediaService()
        {
            ffmpeg.RootPath = Environment.GetEnvironmentVariable("FFMPEG_ROOT") ?? "";
        }

        public unsafe MediaAsset Probe(string path)
        {
            AVFormatContext* inCtx = null;
            var probe = new MediaAsset { Url = path, Hash = HashFile(path) };
            try
            {
                var pIn = inCtx;
                ThrowIfError(ffmpeg.avformat_open_input(&pIn, path, null, null), "avformat_open_input");
                inCtx = pIn;
                ThrowIfError(ffmpeg.avformat_find_stream_info(inCtx, null), "avformat_find_stream_info");

                var vIdx = ffmpeg.av_find_best_stream(inCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (vIdx < 0)
                    throw new InvalidOperationException("No video stream found");

                var stream = inCtx->streams[vIdx];
                var codec = stream->codecpar;

                var fpsNum = codec->framerate.num;
                var fpsDen = codec->framerate.den;

                double duration = inCtx->duration > 0
                    ? inCtx->duration * ffmpeg.av_q2d(ffmpeg.av_get_time_base_q())
                    : stream->duration * ffmpeg.av_q2d(stream->time_base);

                probe.DurationSec = duration;
                probe.Width = codec->width;
                probe.Height = codec->height;
                return probe;
            }
            finally
            {
                if (inCtx != null)
                {
                    var pIn = inCtx;
                    ffmpeg.avformat_close_input(&pIn);
                }
            }
        }

        public static string HashFile(string path)
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        public unsafe void RenderClip(string sourcePath, Clip clip, string outputPath, int width, int height)
        {
            AVFormatContext* inCtx = null;
            AVFormatContext* outCtx = null;
            AVCodecContext* decCtx = null;
            AVCodecContext* encCtx = null;
            SwsContext* sws = null;
            AVFrame* inFrame = null;
            AVFrame* outFrame = null;
            AVPacket* packet = null;

            try
            {
                // ---- open input ----
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
                ThrowIfError(ffmpeg.avcodec_open2(decCtx, dec, null), "avcodec_open2");

                // ---- open output ----
                ThrowIfError(ffmpeg.avformat_alloc_output_context2(&outCtx, null, null, outputPath), "avformat_alloc_output_context2");

                var enc = ffmpeg.avcodec_find_encoder_by_name("libx264");
                if (enc == null)
                    throw new InvalidOperationException("libx264 encoder not found");
                encCtx = ffmpeg.avcodec_alloc_context3(enc);
                encCtx->width = width;
                encCtx->height = height;
                encCtx->time_base = new AVRational { num = 1, den = 30 };
                encCtx->framerate = new AVRational { num = 30, den = 1 };
                encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                ThrowIfError(ffmpeg.avcodec_open2(encCtx, enc, null), "avcodec_open2");

                var outStream = ffmpeg.avformat_new_stream(outCtx, enc);
                ThrowIfError(ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx), "avcodec_parameters_from_context");
                outStream->time_base = encCtx->time_base;

                ThrowIfError(ffmpeg.avio_open(&outCtx->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE), "avio_open");
                ThrowIfError(ffmpeg.avformat_write_header(outCtx, null), "avformat_write_header");

                // ---- pixel conversion ----
                sws = ffmpeg.sws_getContext(
                    inCodecPar->width, inCodecPar->height, decCtx->pix_fmt,
                    width, height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    SWS_BILINEAR, null, null, null);

                inFrame = ffmpeg.av_frame_alloc();
                outFrame = ffmpeg.av_frame_alloc();
                outFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                outFrame->width = width;
                outFrame->height = height;
                ThrowIfError(ffmpeg.av_frame_get_buffer(outFrame, 32), "av_frame_get_buffer");

                packet = ffmpeg.av_packet_alloc();

                var tbNum = inStream->time_base.num;
                var tbDen = inStream->time_base.den;

                long ToTs(double seconds) => (long)(seconds * tbDen / tbNum);

                var startTs = ToTs(clip.SourceIn);
                var endTs = ToTs(clip.SourceOut);
                ffmpeg.av_seek_frame(inCtx, vIdx, Math.Max(0, startTs - tbDen), ffmpeg.AVSEEK_FLAG_BACKWARD);

                var started = false;
                long outPts = 0;

                while (ffmpeg.av_read_frame(inCtx, packet) >= 0)
                {
                    if (packet->stream_index == vIdx)
                    {
                        var sendRet = ffmpeg.avcodec_send_packet(decCtx, packet);
                        if (sendRet >= 0)
                        {
                            while (ffmpeg.avcodec_receive_frame(decCtx, inFrame) == 0)
                            {
                                var frameTs = inFrame->best_effort_timestamp;
                                if (frameTs >= startTs && frameTs < endTs)
                                {
                                    ffmpeg.sws_scale(sws, inFrame->data, inFrame->linesize, 0, inFrame->height,
                                        outFrame->data, outFrame->linesize);
                                    outPts = EncodeFrame(encCtx, outCtx, outStream, outFrame, outPts);
                                    started = true;
                                }
                                else if (started && frameTs >= endTs)
                                {
                                    goto done;
                                }
                            }
                        }
                    }
                    ffmpeg.av_packet_unref(packet);
                }

            done:
                EncodeFrame(encCtx, outCtx, outStream, null, outPts);
                var flushPkt = ffmpeg.av_packet_alloc();
                while (ffmpeg.avcodec_receive_packet(encCtx, flushPkt) == 0)
                {
                    flushPkt->stream_index = 0;
                    ffmpeg.av_packet_rescale_ts(flushPkt, encCtx->time_base, outStream->time_base);
                    ffmpeg.av_interleaved_write_frame(outCtx, flushPkt);
                }
                ffmpeg.av_packet_free(&flushPkt);

                ffmpeg.av_write_trailer(outCtx);
            }
            finally
            {
                if (packet != null) ffmpeg.av_packet_free(&packet);
                if (inFrame != null) ffmpeg.av_frame_free(&inFrame);
                if (outFrame != null) ffmpeg.av_frame_free(&outFrame);
                if (sws != null) ffmpeg.sws_freeContext(sws);
                if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
                if (encCtx != null) ffmpeg.avcodec_free_context(&encCtx);
                if (inCtx != null)
                {
                    var pIn = inCtx;
                    ffmpeg.avformat_close_input(&pIn);
                }
                if (outCtx != null) ffmpeg.avformat_free_context(outCtx);
            }
        }

        public unsafe double AverageLuma(string path, double seconds)
        {
            AVFormatContext* inCtx = null;
            AVCodecContext* decCtx = null;
            AVFrame* frame = null;
            AVPacket* packet = null;
            SwsContext* sws = null;
            AVFrame* rgb = null;

            try
            {
                var pIn = inCtx;
                ThrowIfError(ffmpeg.avformat_open_input(&pIn, path, null, null), "avformat_open_input");
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
                ThrowIfError(ffmpeg.avcodec_open2(decCtx, dec, null), "avcodec_open2");

                var targetTs = (long)(seconds * inStream->time_base.den / inStream->time_base.num);
                ffmpeg.av_seek_frame(inCtx, vIdx, Math.Max(0, targetTs - 1), ffmpeg.AVSEEK_FLAG_BACKWARD);

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                while (ffmpeg.av_read_frame(inCtx, packet) >= 0)
                {
                    if (packet->stream_index == vIdx && ffmpeg.avcodec_send_packet(decCtx, packet) >= 0)
                    {
                        while (ffmpeg.avcodec_receive_frame(decCtx, frame) == 0)
                        {
                            var ts = frame->best_effort_timestamp;
                            if (ts >= targetTs || ts < 0)
                            {
                                var w = inCodecPar->width;
                                var h = inCodecPar->height;
                                rgb = ffmpeg.av_frame_alloc();
                                rgb->format = (int)AVPixelFormat.AV_PIX_FMT_GRAY8;
                                rgb->width = 64;
                                rgb->height = 36;
                                ffmpeg.av_frame_get_buffer(rgb, 32);
                                sws = ffmpeg.sws_getContext(w, h, decCtx->pix_fmt, 64, 36, AVPixelFormat.AV_PIX_FMT_GRAY8, SWS_BILINEAR, null, null, null);
                                ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, h, rgb->data, rgb->linesize);

                                long sum = 0;
                                for (var y = 0; y < 36; y++)
                                {
                                    var row = rgb->data[0] + y * rgb->linesize[0];
                                    for (var x = 0; x < 64; x++)
                                        sum += row[x];
                                }
                                return sum / (double)(64 * 36);
                            }
                        }
                    }
                    ffmpeg.av_packet_unref(packet);
                }

                return 0;
            }
            finally
            {
                if (rgb != null) ffmpeg.av_frame_free(&rgb);
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

        private static unsafe long EncodeFrame(AVCodecContext* encCtx, AVFormatContext* outCtx, AVStream* outStream, AVFrame* frame, long outPts)
        {
            if (frame is not null)
            {
                frame->pts = outPts;
                var r = ffmpeg.avcodec_send_frame(encCtx, frame);
                if (r < 0)
                    return outPts + 1;
                outPts++;
            }
            else
            {
                ffmpeg.avcodec_send_frame(encCtx, null);
            }

            var encPkt = ffmpeg.av_packet_alloc();
            while (ffmpeg.avcodec_receive_packet(encCtx, encPkt) == 0)
            {
                encPkt->stream_index = 0;
                ffmpeg.av_packet_rescale_ts(encPkt, encCtx->time_base, outStream->time_base);
                ffmpeg.av_interleaved_write_frame(outCtx, encPkt);
            }
            ffmpeg.av_packet_free(&encPkt);
            return outPts;
        }

        private static void ThrowIfError(int ret, string what)
        {
            if (ret < 0)
                throw new InvalidOperationException($"{what} failed: {ret}");
        }
    }
}
