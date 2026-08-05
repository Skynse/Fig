using System;
using System.Threading;
using FFmpeg.AutoGen;

namespace Fig.Core.Media
{
    /// <summary>
    /// A persistent sequential video decoder. The ffmpeg context stays open so playback
    /// decodes forward without re-seeking on every frame. A backward jump (scrub) triggers
    /// a random-access seek; a forward request just keeps decoding.
    /// </summary>
    public sealed unsafe class VideoFrameSource : IVideoFrameSource
    {
        private const int SWS_BILINEAR = 2;

        private readonly AVFormatContext* _inCtx;
        private readonly AVCodecContext* _decCtx;
        private readonly int _vIdx;
        private readonly AVStream* _stream;
        private readonly int _srcW, _srcH;
        private readonly int _outW, _outH;
        private readonly AVRational _timeBase;

        private AVFrame* _pendingFrame;
        private double _lastPresentedSec = -1;
        private bool _disposed;

        public double LastPresentedTimeSec => _lastPresentedSec;

        internal VideoFrameSource(string sourcePath, int width, int height)
        {
            AVFormatContext* inCtx = null;
            AVCodecContext* decCtx = null;

            try
            {
                var pIn = inCtx;
                ThrowIfError(ffmpeg.avformat_open_input(&pIn, sourcePath, null, null), "avformat_open_input");
                inCtx = pIn;
                ThrowIfError(ffmpeg.avformat_find_stream_info(inCtx, null), "avformat_find_stream_info");

                var vIdx = ffmpeg.av_find_best_stream(inCtx, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
                ThrowIfError(vIdx, "av_find_best_stream(video)");
                var stream = inCtx->streams[vIdx];
                var par = stream->codecpar;

                var dec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (dec == null)
                    throw new InvalidOperationException("No video decoder found");
                decCtx = ffmpeg.avcodec_alloc_context3(dec);
                ThrowIfError(ffmpeg.avcodec_parameters_to_context(decCtx, par), "avcodec_parameters_to_context");
                decCtx->pkt_timebase = stream->time_base;
                ThrowIfError(ffmpeg.avcodec_open2(decCtx, dec, null), "avcodec_open2");

                _inCtx = inCtx;
                _decCtx = decCtx;
                _vIdx = vIdx;
                _stream = stream;
                _srcW = par->width;
                _srcH = par->height;
                _outW = width;
                _outH = height;
                _timeBase = stream->time_base;

                _pendingFrame = ffmpeg.av_frame_alloc();
            }
            catch
            {
                if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
                if (inCtx != null)
                {
                    var p = inCtx;
                    ffmpeg.avformat_close_input(&p);
                }
                throw;
            }
        }

        public void Seek(double timeSec)
        {
            var ctx = _inCtx;
            var dec = _decCtx;
            var targetTs = (long)(Math.Max(0, timeSec) * _timeBase.den / _timeBase.num);
            var ret = ffmpeg.av_seek_frame(ctx, _vIdx, Math.Max(0, targetTs), ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (ret < 0)
                ffmpeg.av_seek_frame(ctx, _vIdx, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(dec);
            _lastPresentedSec = -1;
        }

        public DecodedFrame? DecodeForward(double timeSec)
        {
            // decode frames until we have one whose timestamp is at/after the requested time
            var ctx = _inCtx;
            var dec = _decCtx;
            AVPacket* packet = ffmpeg.av_packet_alloc();
            AVFrame* rgb = null;
            SwsContext* sws = null;
            try
            {
                // keep a presented frame from the previous call if we still haven't caught up
                if (_lastPresentedSec >= 0 && timeSec <= _lastPresentedSec)
                    return null;   // caller should seek back; don't rewind implicitly

                while (true)
                {
                    var got = false;
                    int ret;
                    while ((ret = ffmpeg.av_read_frame(ctx, packet)) >= 0)
                    {
                        if (packet->stream_index == _vIdx && ffmpeg.avcodec_send_packet(dec, packet) >= 0)
                        {
                            while (ffmpeg.avcodec_receive_frame(dec, _pendingFrame) == 0)
                            {
                                var ts = _pendingFrame->best_effort_timestamp;
                                var sec = ts * ffmpeg.av_q2d(_timeBase);
                                if (sec >= timeSec)
                                {
                                    got = true;
                                    _lastPresentedSec = sec;
                                    break;
                                }
                            }
                            if (got) break;
                        }
                        ffmpeg.av_packet_unref(packet);
                    }

                    if (!got)
                    {
                        // flush decoder for trailing frames
                        ffmpeg.avcodec_send_packet(dec, null);
                        while (ffmpeg.avcodec_receive_frame(dec, _pendingFrame) == 0)
                        {
                            var sec = _pendingFrame->best_effort_timestamp * ffmpeg.av_q2d(_timeBase);
                            if (sec >= timeSec)
                            {
                                got = true;
                                _lastPresentedSec = sec;
                                break;
                            }
                        }
                    }

                    if (!got)
                        return null;   // EOF

                    // we have the frame; scale+convert
                    rgb = ffmpeg.av_frame_alloc();
                    rgb->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
                    rgb->width = _outW;
                    rgb->height = _outH;
                    ThrowIfError(ffmpeg.av_frame_get_buffer(rgb, 32), "av_frame_get_buffer");

                    sws = ffmpeg.sws_getContext(_srcW, _srcH, _decCtx->pix_fmt, _outW, _outH, AVPixelFormat.AV_PIX_FMT_BGRA, SWS_BILINEAR, null, null, null);
                    if (sws == null)
                        return null;
                    ffmpeg.sws_scale(sws, _pendingFrame->data, _pendingFrame->linesize, 0, _srcH, rgb->data, rgb->linesize);

                    var bytes = new byte[_outW * _outH * 4];
                    var rowSize = _outW * 4;
                    for (var y = 0; y < _outH; y++)
                    {
                        var src = rgb->data[0] + y * rgb->linesize[0];
                        var dst = y * rowSize;
                        for (var x = 0; x < rowSize; x++)
                            bytes[dst + x] = src[x];
                    }

                    return new DecodedFrame { Width = _outW, Height = _outH, Pixels = bytes };
                }
            }
            finally
            {
                if (sws != null) ffmpeg.sws_freeContext(sws);
                if (rgb != null) ffmpeg.av_frame_free(&rgb);
                ffmpeg.av_packet_free(&packet);
            }
        }

        private static void ThrowIfError(int ret, string what)
        {
            if (ret < 0)
                throw new InvalidOperationException($"{what} failed: {ret}");
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_pendingFrame != null)
            {
                var f = _pendingFrame;
                ffmpeg.av_frame_free(&f);
            }
            if (_decCtx != null)
            {
                var d = _decCtx;
                ffmpeg.avcodec_free_context(&d);
            }
            if (_inCtx != null)
            {
                var p = _inCtx;
                ffmpeg.avformat_close_input(&p);
            }
        }
    }
}
