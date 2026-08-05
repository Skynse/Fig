using System;
using FFmpeg.AutoGen;

namespace Fig.Core.Media
{
    /// <summary>
    /// Persistent sequential audio decoder. Keeps the ffmpeg demux/decode/resample
    /// context open so playback can pull contiguous chunks without re-seeking on
    /// every mix window (since re-seeking was the main source of crackles).
    /// </summary>
    public sealed unsafe class AudioSampleSource : IAudioSampleSource
    {
        /// <summary>If the next request starts farther than this from where we left off, re-seek.</summary>
        private const double ContiguityEpsSec = 0.002;

        private readonly AVFormatContext* _inCtx;
        private readonly AVCodecContext* _decCtx;
        private readonly SwrContext* _swr;
        private readonly int _aIdx;
        private readonly int _sampleRate;
        private readonly AVRational _timeBase;

        private AVFrame* _frame;
        private AVPacket* _packet;

        // leftover resampled floats from the previous decode call (interleaved L/R)
        private float[] _carry = Array.Empty<float>();
        private int _carryOffset;

        private double _nextSec = -1;
        private bool _skipToPts;
        private long _skipPts;
        private bool _disposed;
        private bool _eof;

        public double NextTimeSec => _nextSec;

        internal AudioSampleSource(string sourcePath, int sampleRate)
        {
            _sampleRate = sampleRate;
            AVFormatContext* inCtx = null;
            AVCodecContext* decCtx = null;
            SwrContext* swr = null;

            try
            {
                var pIn = inCtx;
                ThrowIfError(ffmpeg.avformat_open_input(&pIn, sourcePath, null, null), "avformat_open_input");
                inCtx = pIn;
                ThrowIfError(ffmpeg.avformat_find_stream_info(inCtx, null), "avformat_find_stream_info");

                var aIdx = ffmpeg.av_find_best_stream(inCtx, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                ThrowIfError(aIdx, "av_find_best_stream(audio)");
                var stream = inCtx->streams[aIdx];
                var par = stream->codecpar;

                var dec = ffmpeg.avcodec_find_decoder(par->codec_id);
                if (dec == null)
                    throw new InvalidOperationException("No audio decoder found");
                decCtx = ffmpeg.avcodec_alloc_context3(dec);
                ThrowIfError(ffmpeg.avcodec_parameters_to_context(decCtx, par), "avcodec_parameters_to_context");
                decCtx->pkt_timebase = stream->time_base;
                ThrowIfError(ffmpeg.avcodec_open2(decCtx, dec, null), "avcodec_open2");

                swr = ffmpeg.swr_alloc();
                AVChannelLayout stereo = default;
                ffmpeg.av_channel_layout_default(&stereo, 2);
                ffmpeg.swr_alloc_set_opts2(&swr,
                    &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, sampleRate,
                    &par->ch_layout, (AVSampleFormat)par->format, par->sample_rate,
                    0, null);
                ThrowIfError(ffmpeg.swr_init(swr), "swr_init");
                ffmpeg.av_channel_layout_uninit(&stereo);

                _inCtx = inCtx;
                _decCtx = decCtx;
                _swr = swr;
                _aIdx = aIdx;
                _timeBase = stream->time_base;
                _frame = ffmpeg.av_frame_alloc();
                _packet = ffmpeg.av_packet_alloc();
            }
            catch
            {
                if (swr != null) ffmpeg.swr_free(&swr);
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
            timeSec = Math.Max(0, timeSec);
            var targetTs = (long)(timeSec * _timeBase.den / _timeBase.num);
            var ret = ffmpeg.av_seek_frame(_inCtx, _aIdx, Math.Max(0, targetTs), ffmpeg.AVSEEK_FLAG_BACKWARD);
            if (ret < 0)
                ffmpeg.av_seek_frame(_inCtx, _aIdx, 0, ffmpeg.AVSEEK_FLAG_BACKWARD);
            ffmpeg.avcodec_flush_buffers(_decCtx);
            _carry = Array.Empty<float>();
            _carryOffset = 0;
            _nextSec = timeSec;
            _skipPts = targetTs;
            _skipToPts = true;
            _eof = false;
        }

        public float[] Read(double startSec, double durationSec)
        {
            var totalFrames = Math.Max(0, (int)Math.Round(durationSec * _sampleRate));
            var output = new float[totalFrames * 2];
            if (totalFrames == 0)
                return output;

            // re-seek when the request isn't contiguous with where we left off
            if (_nextSec < 0 || Math.Abs(startSec - _nextSec) > ContiguityEpsSec)
                Seek(startSec);

            var written = DrainCarry(output, 0);

            while (written < output.Length && !_eof)
            {
                var ret = ffmpeg.av_read_frame(_inCtx, _packet);
                if (ret < 0)
                {
                    ffmpeg.avcodec_send_packet(_decCtx, null);
                    written = DrainDecoder(output, written);
                    written = FlushResampler(output, written);
                    _eof = true;
                    break;
                }

                try
                {
                    if (_packet->stream_index != _aIdx)
                        continue;
                    if (ffmpeg.avcodec_send_packet(_decCtx, _packet) < 0)
                        continue;
                    written = DrainDecoder(output, written);
                }
                finally
                {
                    ffmpeg.av_packet_unref(_packet);
                }
            }

            _nextSec = startSec + (written / 2.0) / _sampleRate;
            // trailing silence already zero-filled when the media ends mid-chunk
            return output;
        }

        private int DrainCarry(float[] output, int written)
        {
            if (_carryOffset >= _carry.Length)
                return written;
            var take = Math.Min(_carry.Length - _carryOffset, output.Length - written);
            Array.Copy(_carry, _carryOffset, output, written, take);
            _carryOffset += take;
            if (_carryOffset >= _carry.Length)
            {
                _carry = Array.Empty<float>();
                _carryOffset = 0;
            }
            return written + take;
        }

        private int DrainDecoder(float[] output, int written)
        {
            while (written < output.Length && ffmpeg.avcodec_receive_frame(_decCtx, _frame) == 0)
            {
                if (_skipToPts)
                {
                    var pts = _frame->best_effort_timestamp;
                    if (pts >= 0 && pts < _skipPts)
                        continue;
                    _skipToPts = false;
                }

                written = ResampleFrame(output, written);
            }
            return written;
        }

        private int FlushResampler(float[] output, int written)
        {
            while (written < output.Length)
            {
                var delay = ffmpeg.swr_get_delay(_swr, _sampleRate);
                if (delay <= 0)
                    break;
                // headroom: swr_convert must never write past the malloc
                var outSamples = (int)delay + 256;
                var outBuf = (byte*)ffmpeg.av_malloc((ulong)outSamples * 2 * sizeof(float));
                if (outBuf == null)
                    break;
                try
                {
                    var got = ffmpeg.swr_convert(_swr, &outBuf, outSamples, null, 0);
                    if (got <= 0)
                        break;
                    written = CopyResampled(output, written, (float*)outBuf, Math.Min(got, outSamples));
                }
                finally
                {
                    ffmpeg.av_free(outBuf);
                }
            }
            return written;
        }

        private int ResampleFrame(float[] output, int written)
        {
            // swr_get_out_samples is an upper-bound estimate that can still undershoot
            // when compensation kicks in — under-allocating corrupts the glibc heap.
            var outSamples = Math.Max(
                ffmpeg.swr_get_out_samples(_swr, _frame->nb_samples) + 256,
                _frame->nb_samples * 4 + 256);
            if (outSamples <= 0)
                return written;
            var outBuf = (byte*)ffmpeg.av_malloc((ulong)outSamples * 2 * sizeof(float));
            if (outBuf == null)
                return written;
            try
            {
                var got = ffmpeg.swr_convert(_swr, &outBuf, outSamples, _frame->extended_data, _frame->nb_samples);
                if (got <= 0)
                    return written;
                return CopyResampled(output, written, (float*)outBuf, Math.Min(got, outSamples));
            }
            finally
            {
                ffmpeg.av_free(outBuf);
            }
        }

        private int CopyResampled(float[] output, int written, float* src, int frames)
        {
            var floats = frames * 2;
            var room = output.Length - written;
            var take = Math.Min(floats, room);
            for (var n = 0; n < take; n++)
                output[written + n] = src[n];
            written += take;

            // stash overflow so the next Read doesn't drop samples at chunk boundaries
            if (take < floats)
            {
                var leftover = floats - take;
                _carry = new float[leftover];
                for (var n = 0; n < leftover; n++)
                    _carry[n] = src[take + n];
                _carryOffset = 0;
            }
            return written;
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
            if (_packet != null)
            {
                var p = _packet;
                ffmpeg.av_packet_free(&p);
                _packet = null;
            }
            if (_frame != null)
            {
                var f = _frame;
                ffmpeg.av_frame_free(&f);
                _frame = null;
            }
            if (_swr != null)
            {
                var s = _swr;
                ffmpeg.swr_free(&s);
            }
            if (_decCtx != null)
            {
                var d = _decCtx;
                ffmpeg.avcodec_free_context(&d);
            }
            if (_inCtx != null)
            {
                var c = _inCtx;
                ffmpeg.avformat_close_input(&c);
            }
        }
    }
}
