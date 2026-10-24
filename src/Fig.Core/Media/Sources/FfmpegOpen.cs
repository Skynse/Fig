using System;
using FFmpeg.AutoGen;

namespace Fig.Core.Media
{
    /// <summary>
    /// The steps every reader of a media file starts with: open it, pick its best stream of a kind, open a decoder for
    /// that stream. Each throws with what failed, and frees what it made if it fails part-way; once they return, the
    /// caller owns the context (and frees it as before).
    /// </summary>
    internal static unsafe class FfmpegOpen
    {
        /// <summary>Opens a file and reads its stream info.</summary>
        public static AVFormatContext* Input(string path)
        {
            AVFormatContext* ctx = null;
            var opened = ffmpeg.avformat_open_input(&ctx, path, null, null);
            if (opened < 0)
                throw new InvalidOperationException($"Couldn't open {System.IO.Path.GetFileName(path)}: {Describe(opened)} ({path})");
            var info = ffmpeg.avformat_find_stream_info(ctx, null);
            if (info < 0)
            {
                ffmpeg.avformat_close_input(&ctx);
                Check(info, "avformat_find_stream_info");
            }
            return ctx;
        }

        /// <summary>The index of the file's best stream of <paramref name="type"/>.</summary>
        public static int BestStream(AVFormatContext* ctx, AVMediaType type)
        {
            var index = ffmpeg.av_find_best_stream(ctx, type, -1, -1, null, 0);
            Check(index, $"av_find_best_stream({(type == AVMediaType.AVMEDIA_TYPE_AUDIO ? "audio" : "video")})");
            return index;
        }

        /// <summary>
        /// A decoder opened for <paramref name="stream"/>. <paramref name="threads"/> above 0 decodes on that many threads;
        /// <paramref name="skip"/> drops frames the caller doesn't need (non-keyframes for a filmstrip).
        /// </summary>
        public static AVCodecContext* Decoder(AVStream* stream, int threads = 0, AVDiscard skip = AVDiscard.AVDISCARD_DEFAULT)
        {
            var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null)
                throw new InvalidOperationException(stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO
                    ? "No audio decoder found" : "No video decoder found");
            var ctx = ffmpeg.avcodec_alloc_context3(codec);
            try
            {
                Check(ffmpeg.avcodec_parameters_to_context(ctx, stream->codecpar), "avcodec_parameters_to_context");
                ctx->pkt_timebase = stream->time_base;
                if (threads > 0)
                    ctx->thread_count = threads;
                ctx->skip_frame = skip;
                Check(ffmpeg.avcodec_open2(ctx, codec, null), "avcodec_open2");
                return ctx;
            }
            catch
            {
                ffmpeg.avcodec_free_context(&ctx);
                throw;
            }
        }

        /// <summary>
        /// A resampler from <paramref name="from"/>'s audio to <paramref name="channels"/> channels of
        /// <paramref name="format"/> at <paramref name="sampleRate"/>.
        /// </summary>
        public static SwrContext* Resampler(AVCodecParameters* from, int channels, AVSampleFormat format, int sampleRate)
        {
            var swr = ffmpeg.swr_alloc();
            AVChannelLayout layout = default;
            ffmpeg.av_channel_layout_default(&layout, channels);
            try
            {
                ffmpeg.swr_alloc_set_opts2(&swr, &layout, format, sampleRate,
                    &from->ch_layout, (AVSampleFormat)from->format, from->sample_rate, 0, null);
                Check(ffmpeg.swr_init(swr), "swr_init");
                return swr;
            }
            catch
            {
                ffmpeg.swr_free(&swr);
                throw;
            }
            finally
            {
                ffmpeg.av_channel_layout_uninit(&layout);
            }
        }

        /// <summary>Throws with what failed when an FFmpeg call returns an error.</summary>
        public static void Check(int ret, string what)
        {
            if (ret < 0)
                throw new InvalidOperationException($"{what} failed: {Describe(ret)}");
        }

        /// <summary>FFmpeg's words for an error code ("No such file or directory"), with the code.</summary>
        public static string Describe(int error)
        {
            const int size = 256;
            var buffer = stackalloc byte[size];
            ffmpeg.av_strerror(error, buffer, size);
            var text = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)buffer);
            return string.IsNullOrWhiteSpace(text) ? error.ToString() : $"{text} ({error})";
        }
    }
}
