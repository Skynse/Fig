using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Fig.Core.Timeline;
using FFmpeg.AutoGen;

namespace Fig.Core.Media
{
    public partial class MediaService : IMediaService
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
                if (vIdx >= 0)
                {
                    var stream = inCtx->streams[vIdx];
                    var codec = stream->codecpar;

                    var fpsNum = codec->framerate.num;
                    var fpsDen = codec->framerate.den;
                    if (fpsNum <= 0 || fpsDen <= 0)
                    {
                        fpsNum = stream->avg_frame_rate.num;
                        fpsDen = stream->avg_frame_rate.den;
                    }

                    double duration = inCtx->duration > 0
                        ? inCtx->duration * ffmpeg.av_q2d(ffmpeg.av_get_time_base_q())
                        : stream->duration * ffmpeg.av_q2d(stream->time_base);

                    probe.DurationSec = duration;
                    probe.Width = codec->width;
                    probe.Height = codec->height;
                    probe.Kind = MediaKind.Video;
                    probe.HasAudio = HasAudioStream(inCtx);
                    if (fpsNum > 0 && fpsDen > 0)
                        probe.SourceRate = new Fig.Core.Timeline.FrameRate(fpsNum, fpsDen);
                    return probe;
                }

                // no video stream: try audio-only
                var aIdx = ffmpeg.av_find_best_stream(inCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                if (aIdx < 0)
                    throw new InvalidOperationException("No video or audio stream found");

                var aStream = inCtx->streams[aIdx];
                probe.DurationSec = inCtx->duration > 0
                    ? inCtx->duration * ffmpeg.av_q2d(ffmpeg.av_get_time_base_q())
                    : aStream->duration * ffmpeg.av_q2d(aStream->time_base);
                probe.Kind = MediaKind.Audio;
                probe.HasAudio = true;
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

        private static unsafe bool HasAudioStream(AVFormatContext* ctx)
        {
            for (var i = 0; i < ctx->nb_streams; i++)
            {
                if (ctx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                    return true;
            }
            return false;
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

        public unsafe void GenerateThumbnail(string sourcePath, string outputPath, int width = 320)
        {
            AVFormatContext* inCtx = null;
            AVCodecContext* decCtx = null;
            AVCodecContext* encCtx = null;
            AVFrame* frame = null;
            AVFrame* outFrame = null;
            AVPacket* packet = null;
            SwsContext* sws = null;

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
                ThrowIfError(ffmpeg.avcodec_open2(decCtx, dec, null), "avcodec_open2");

                // seek ~1s in, fall back to 0
                var targetSec = 1.0;
                var targetTs = (long)(targetSec * inStream->time_base.den / inStream->time_base.num);
                var seekRet = ffmpeg.av_seek_frame(inCtx, vIdx, Math.Max(0, targetTs), ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (seekRet < 0)
                    ffmpeg.av_seek_frame(inCtx, vIdx, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                var got = false;
                while (!got && ffmpeg.av_read_frame(inCtx, packet) >= 0)
                {
                    if (packet->stream_index == vIdx && ffmpeg.avcodec_send_packet(decCtx, packet) >= 0)
                    {
                        while (!got && ffmpeg.avcodec_receive_frame(decCtx, frame) == 0)
                        {
                            var w = inCodecPar->width;
                            var h = inCodecPar->height;
                            var th = (int)Math.Round(h * (width / (double)w));

                            outFrame = ffmpeg.av_frame_alloc();
                            outFrame->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                            outFrame->color_range = AVColorRange.AVCOL_RANGE_JPEG;
                            outFrame->width = width;
                            outFrame->height = th;
                            ThrowIfError(ffmpeg.av_frame_get_buffer(outFrame, 32), "av_frame_get_buffer");

                            sws = ffmpeg.sws_getContext(w, h, decCtx->pix_fmt, width, th, AVPixelFormat.AV_PIX_FMT_YUV420P, SWS_BILINEAR, null, null, null);
                            ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, h, outFrame->data, outFrame->linesize);

                            // mjpeg single-frame encode
                            var enc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
                            if (enc == null)
                                throw new InvalidOperationException("No mjpeg encoder found");
                            encCtx = ffmpeg.avcodec_alloc_context3(enc);
                            encCtx->width = width;
                            encCtx->height = th;
                            encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                            encCtx->color_range = AVColorRange.AVCOL_RANGE_JPEG;
                            encCtx->time_base = new AVRational { num = 1, den = 30 };
                            encCtx->qmin = 3;
                            encCtx->qmax = 5;
                            ThrowIfError(ffmpeg.avcodec_open2(encCtx, enc, null), "avcodec_open2");

                            outFrame->pts = 0;
                            ThrowIfError(ffmpeg.avcodec_send_frame(encCtx, outFrame), "avcodec_send_frame");

                            var outPkt = ffmpeg.av_packet_alloc();
                            while (ffmpeg.avcodec_receive_packet(encCtx, outPkt) == 0)
                            {
                                using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                                fs.Write(new ReadOnlySpan<byte>(outPkt->data, outPkt->size));
                            }
                            ffmpeg.av_packet_free(&outPkt);
                            got = true;
                        }
                    }
                    ffmpeg.av_packet_unref(packet);
                }
            }
            finally
            {
                if (sws != null) ffmpeg.sws_freeContext(sws);
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (outFrame != null) ffmpeg.av_frame_free(&outFrame);
                if (packet != null) ffmpeg.av_packet_free(&packet);
                if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
                if (encCtx != null) ffmpeg.avcodec_free_context(&encCtx);
                if (inCtx != null)
                {
                    var pIn = inCtx;
                    ffmpeg.avformat_close_input(&pIn);
                }
            }
        }

        public unsafe DecodedFrame? DecodeFrameAt(string sourcePath, double timeSec, int width, int height)
        {
            AVFormatContext* inCtx = null;
            AVCodecContext* decCtx = null;
            AVFrame* frame = null;
            AVFrame* rgb = null;
            AVPacket* packet = null;
            SwsContext* sws = null;

            try
            {
                var pIn = inCtx;
                ThrowIfError(ffmpeg.avformat_open_input(&pIn, sourcePath, null, null), "avformat_open_input");
                inCtx = pIn;
                ThrowIfError(ffmpeg.avformat_find_stream_info(inCtx, null), "avformat_find_stream_info");

                var vIdx = ffmpeg.av_find_best_stream(inCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                ThrowIfError(vIdx, "av_find_best_stream(video)");
                var inStream = inCtx->streams[vIdx];
                var inCodecPar = inStream->codecpar;

                var dec = ffmpeg.avcodec_find_decoder(inCodecPar->codec_id);
                if (dec == null)
                    throw new InvalidOperationException("No video decoder found");
                decCtx = ffmpeg.avcodec_alloc_context3(dec);
                ThrowIfError(ffmpeg.avcodec_parameters_to_context(decCtx, inCodecPar), "avcodec_parameters_to_context");
                decCtx->pkt_timebase = inStream->time_base;
                ThrowIfError(ffmpeg.avcodec_open2(decCtx, dec, null), "avcodec_open2");

                // seek to the requested time (backward to keyframe, then decode forward)
                var targetTs = (long)(timeSec * inStream->time_base.den / inStream->time_base.num);
                var seekRet = ffmpeg.av_seek_frame(inCtx, vIdx, Math.Max(0, targetTs), ffmpeg.AVSEEK_FLAG_BACKWARD);
                if (seekRet < 0)
                    ffmpeg.av_seek_frame(inCtx, vIdx, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
                ffmpeg.avcodec_flush_buffers(decCtx);

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                var got = false;
                while (!got && ffmpeg.av_read_frame(inCtx, packet) >= 0)
                {
                    if (packet->stream_index == vIdx && ffmpeg.avcodec_send_packet(decCtx, packet) >= 0)
                    {
                        while (!got && ffmpeg.avcodec_receive_frame(decCtx, frame) == 0)
                        {
                            // take the first frame at/after the target
                            got = true;
                        }
                    }
                    ffmpeg.av_packet_unref(packet);
                }
                if (!got)
                    return null;

                // scale to requested size and convert to BGRA
                rgb = ffmpeg.av_frame_alloc();
                rgb->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                rgb->width = width;
                rgb->height = height;
                ThrowIfError(ffmpeg.av_frame_get_buffer(rgb, 32), "av_frame_get_buffer");

                sws = ffmpeg.sws_getContext(
                    inCodecPar->width, inCodecPar->height, decCtx->pix_fmt,
                    width, height, AVPixelFormat.AV_PIX_FMT_BGRA, SWS_BILINEAR, null, null, null);
                if (sws == null)
                    return null;

                ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, inCodecPar->height, rgb->data, rgb->linesize);

                var bytes = new byte[width * height * 4];
                var rowSize = width * 4;
                for (var y = 0; y < height; y++)
                {
                    var src = rgb->data[0] + y * rgb->linesize[0];
                    var dst = y * rowSize;
                    for (var x = 0; x < rowSize; x++)
                        bytes[dst + x] = src[x];
                }

                return new DecodedFrame { Width = width, Height = height, Pixels = bytes };
            }
            finally
            {
                if (sws != null) ffmpeg.sws_freeContext(sws);
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (rgb != null) ffmpeg.av_frame_free(&rgb);
                if (packet != null) ffmpeg.av_packet_free(&packet);
                if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
                if (inCtx != null)
                {
                    var pIn = inCtx;
                    ffmpeg.avformat_close_input(&pIn);
                }
            }
        }

        public IVideoFrameSource OpenVideoSource(string sourcePath, int width, int height)
        {
            return new VideoFrameSource(sourcePath, width, height);
        }

        public IAudioSampleSource OpenAudioSource(string sourcePath, int sampleRate = 48000)
        {
            return new AudioSampleSource(sourcePath, sampleRate);
        }

        public unsafe float[] DecodeSamples(string sourcePath, double startSec, double durationSec, int sampleRate = 48000)
        {
            // one-shot helper for tests/peaks; playback should use OpenAudioSource
            using var source = OpenAudioSource(sourcePath, sampleRate);
            return source.Read(startSec, durationSec);
        }

        public unsafe void SaveFrameAsJpeg(string sourcePath, double timeSec, string outputPath, int width = 320)
        {
            var (srcW, srcH) = ProbeDimensions(sourcePath);
            if (srcW <= 0 || srcH <= 0)
                throw new InvalidOperationException("Could not determine source dimensions");
            var height = Math.Max(2, (int)Math.Round(srcH * (width / (double)srcW)) & ~1);
            width = Math.Max(2, width & ~1);
            var decoded = DecodeFrameAt(sourcePath, timeSec, width, height);
            if (decoded is null)
                throw new InvalidOperationException("Could not decode frame");
            EncodeBgraAsJpeg(decoded, outputPath);
        }

        private static unsafe void EncodeBgraAsJpeg(DecodedFrame frame, string outputPath)
        {
            AVCodecContext* encCtx = null;
            AVFrame* yuv = null;
            AVFrame* bgra = null;
            SwsContext* sws = null;
            try
            {
                var w = Math.Max(2, frame.Width & ~1);
                var h = Math.Max(2, frame.Height & ~1);

                // wrap the BGRA bytes in a frame
                bgra = ffmpeg.av_frame_alloc();
                bgra->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                bgra->width = w;
                bgra->height = h;
                ThrowIfError(ffmpeg.av_frame_get_buffer(bgra, 32), "av_frame_get_buffer");
                var linesize = bgra->linesize[0];
                var srcStride = frame.Width * 4;
                for (var y = 0; y < h; y++)
                {
                    var src = y * srcStride;
                    var dst = bgra->data[0] + y * linesize;
                    for (var x = 0; x < w * 4; x++)
                        dst[x] = frame.Pixels[src + x];
                }

                var enc = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_MJPEG);
                if (enc == null)
                    throw new InvalidOperationException("No mjpeg encoder found");
                encCtx = ffmpeg.avcodec_alloc_context3(enc);
                encCtx->width = w;
                encCtx->height = h;
                encCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
                encCtx->color_range = AVColorRange.AVCOL_RANGE_JPEG;
                encCtx->time_base = new AVRational { num = 1, den = 30 };
                encCtx->qmin = 3;
                encCtx->qmax = 5;
                ThrowIfError(ffmpeg.avcodec_open2(encCtx, enc, null), "avcodec_open2");

                yuv = ffmpeg.av_frame_alloc();
                yuv->format = (int)AVPixelFormat.AV_PIX_FMT_YUV420P;
                yuv->color_range = AVColorRange.AVCOL_RANGE_JPEG;
                yuv->width = w;
                yuv->height = h;
                ThrowIfError(ffmpeg.av_frame_get_buffer(yuv, 32), "av_frame_get_buffer");

                sws = ffmpeg.sws_getContext(w, h, AVPixelFormat.AV_PIX_FMT_BGRA, w, h, AVPixelFormat.AV_PIX_FMT_YUV420P, SWS_BILINEAR, null, null, null);
                if (sws == null)
                    return;
                ffmpeg.sws_scale(sws, bgra->data, bgra->linesize, 0, h, yuv->data, yuv->linesize);

                yuv->pts = 0;
                ThrowIfError(ffmpeg.avcodec_send_frame(encCtx, yuv), "avcodec_send_frame");

                var outPkt = ffmpeg.av_packet_alloc();
                try
                {
                    while (ffmpeg.avcodec_receive_packet(encCtx, outPkt) == 0)
                    {
                        using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                        fs.Write(new ReadOnlySpan<byte>(outPkt->data, outPkt->size));
                    }
                }
                finally
                {
                    ffmpeg.av_packet_free(&outPkt);
                }
            }
            finally
            {
                if (sws != null) ffmpeg.sws_freeContext(sws);
                if (yuv != null) ffmpeg.av_frame_free(&yuv);
                if (bgra != null) ffmpeg.av_frame_free(&bgra);
                if (encCtx != null)
                {
                    var p = encCtx;
                    ffmpeg.avcodec_free_context(&p);
                }
            }
        }

        private static unsafe (int Width, int Height) ProbeDimensions(string sourcePath)
        {
            AVFormatContext* inCtx = null;
            try
            {
                var pIn = inCtx;
                ffmpeg.avformat_open_input(&pIn, sourcePath, null, null);
                inCtx = pIn;
                ffmpeg.avformat_find_stream_info(inCtx, null);
                var vIdx = ffmpeg.av_find_best_stream(inCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                if (vIdx < 0)
                    return (0, 0);
                var par = inCtx->streams[vIdx]->codecpar;
                return (par->width, par->height);
            }
            finally
            {
                if (inCtx != null)
                {
                    var p = inCtx;
                    ffmpeg.avformat_close_input(&p);
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

        public unsafe float[] ExtractPeaks(string sourcePath, int buckets)
        {
            AVFormatContext* inCtx = null;
            AVCodecContext* decCtx = null;
            AVFrame* frame = null;
            AVPacket* packet = null;
            SwrContext* swr = null;

            try
            {
                var pIn = inCtx;
                ThrowIfError(ffmpeg.avformat_open_input(&pIn, sourcePath, null, null), "avformat_open_input");
                inCtx = pIn;
                ThrowIfError(ffmpeg.avformat_find_stream_info(inCtx, null), "avformat_find_stream_info");

                var aIdx = ffmpeg.av_find_best_stream(inCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                ThrowIfError(aIdx, "av_find_best_stream(audio)");
                var inStream = inCtx->streams[aIdx];
                var inCodecPar = inStream->codecpar;

                var dec = ffmpeg.avcodec_find_decoder(inCodecPar->codec_id);
                if (dec == null)
                    throw new InvalidOperationException("No audio decoder found");
                decCtx = ffmpeg.avcodec_alloc_context3(dec);
                ThrowIfError(ffmpeg.avcodec_parameters_to_context(decCtx, inCodecPar), "avcodec_parameters_to_context");
                decCtx->pkt_timebase = inStream->time_base;
                ThrowIfError(ffmpeg.avcodec_open2(decCtx, dec, null), "avcodec_open2");

                // resample everything to mono S16 so peak math is uniform
                swr = ffmpeg.swr_alloc();
                AVChannelLayout monoLayout = default;
                ffmpeg.av_channel_layout_default(&monoLayout, 1);
                ffmpeg.swr_alloc_set_opts2(&swr,
                    &monoLayout, AVSampleFormat.AV_SAMPLE_FMT_S16, 44100,
                    &inCodecPar->ch_layout, (AVSampleFormat)inCodecPar->format, inCodecPar->sample_rate,
                    0, null);
                ThrowIfError(ffmpeg.swr_init(swr), "swr_init");
                ffmpeg.av_channel_layout_uninit(&monoLayout);

                var duration = inCtx->duration > 0
                    ? inCtx->duration * ffmpeg.av_q2d(ffmpeg.av_get_time_base_q())
                    : inStream->duration * ffmpeg.av_q2d(inStream->time_base);
                if (duration <= 0)
                    throw new InvalidOperationException("Cannot determine duration");

                var totalSamples = (long)(44100 * duration);
                var samplesPerBucket = Math.Max(1, totalSamples / buckets);

                var mins = new float[buckets];
                var maxs = new float[buckets];
                for (var i = 0; i < buckets; i++) { mins[i] = 1f; maxs[i] = 0f; }

                long bucketIndex = 0;
                long sampleInBucket = 0;

                frame = ffmpeg.av_frame_alloc();
                packet = ffmpeg.av_packet_alloc();

                while (ffmpeg.av_read_frame(inCtx, packet) >= 0)
                {
                    if (packet->stream_index == aIdx && ffmpeg.avcodec_send_packet(decCtx, packet) >= 0)
                    {
                        while (ffmpeg.avcodec_receive_frame(decCtx, frame) == 0)
                        {
                            // swr_get_out_samples is an estimate — under-allocating lets
                            // swr_convert smash the heap ("corrupted size vs. prev_size").
                            var outSamples = Math.Max(
                                ffmpeg.swr_get_out_samples(swr, frame->nb_samples) + 256,
                                frame->nb_samples * 4 + 256);
                            if (outSamples <= 0)
                                continue;
                            var outBuf = (byte*)ffmpeg.av_malloc((ulong)outSamples * sizeof(short));
                            if (outBuf == null)
                                throw new OutOfMemoryException("swr output buffer");

                            try
                            {
                                var ret = ffmpeg.swr_convert(swr, &outBuf, outSamples, frame->extended_data, frame->nb_samples);
                                if (ret > 0)
                                {
                                    // never read past what we allocated
                                    var nMax = Math.Min(ret, outSamples);
                                    var samples = (short*)outBuf;
                                    for (var n = 0; n < nMax; n++)
                                    {
                                        var val = Math.Abs(samples[n]) / 32768f;
                                        if (val > maxs[bucketIndex]) maxs[bucketIndex] = val;
                                        if (val < mins[bucketIndex]) mins[bucketIndex] = val;

                                        sampleInBucket++;
                                        if (sampleInBucket >= samplesPerBucket && bucketIndex < buckets - 1)
                                        {
                                            bucketIndex++;
                                            sampleInBucket = 0;
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                ffmpeg.av_free(outBuf);
                            }
                        }
                    }
                    ffmpeg.av_packet_unref(packet);
                }

                var peaks = new float[buckets];
                for (var i = 0; i < buckets; i++)
                    peaks[i] = maxs[i];

                return peaks;
            }
            finally
            {
                if (packet != null) ffmpeg.av_packet_free(&packet);
                if (frame != null) ffmpeg.av_frame_free(&frame);
                if (swr != null) ffmpeg.swr_free(&swr);
                if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
                if (inCtx != null)
                {
                    var pIn = inCtx;
                    ffmpeg.avformat_close_input(&pIn);
                }
            }
        }
    }
}
