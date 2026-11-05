using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Fig.Core.Audio;
using Fig.Core.Timeline;
using Whisper.net;
using TimelineModel = Fig.Core.Timeline.Timeline;

namespace Fig.Core.Captions
{
    /// <summary>What to listen for: the language spoken ("auto" detects it), and whether to write English instead.</summary>
    public sealed record TranscribeOptions(string Language = "auto", bool TranslateToEnglish = false);

    /// <summary>
    /// Speech to timed words with Whisper (whisper.cpp, on the CPU). It listens to the timeline as it plays (the mix,
    /// so cuts, speed changes and muted tracks are heard as they are), and times each word on the timeline.
    /// </summary>
    public static class SpeechTranscriber
    {
        /// <summary>Whisper listens at 16 kHz, in mono.</summary>
        public const int SampleRate = 16000;

        /// <summary>
        /// The timeline's sound from <paramref name="startSec"/> for <paramref name="durationSec"/>, mixed as it plays
        /// and brought down to 16 kHz mono. <paramref name="progress"/> gets 0..1.
        /// </summary>
        public static float[] MixForSpeech(AudioMixer mixer, TimelineModel timeline, double startSec, double durationSec,
            IProgress<double>? progress = null, CancellationToken cancel = default)
        {
            const int factor = AudioMixer.SampleRate / SampleRate; // 3
            const double chunkSec = 10;
            var output = new List<float>((int)(durationSec * SampleRate) + SampleRate);
            for (var pos = 0.0; pos < durationSec - 1e-6; pos += chunkSec)
            {
                cancel.ThrowIfCancellationRequested();
                var stereo = mixer.Mix(timeline, startSec + pos, Math.Min(chunkSec, durationSec - pos));
                // each output sample is the average of three input frames (both channels): a box filter that keeps
                // speech and cuts most of what would fold back above 8 kHz
                for (var i = 0; i + factor * 2 <= stereo.Length; i += factor * 2)
                {
                    var sum = 0f;
                    for (var j = 0; j < factor * 2; j++)
                        sum += stereo[i + j];
                    output.Add(sum / (factor * 2));
                }
                progress?.Report(Math.Min(1, (pos + chunkSec) / durationSec));
            }
            return output.ToArray();
        }

        /// <summary>
        /// Transcribes 16 kHz mono <paramref name="samples"/> with the model at <paramref name="modelPath"/>, word by
        /// word. Times are seconds from the first sample plus <paramref name="offsetSec"/>. Each word keeps the space
        /// before it (none in languages written without spaces), so joining them gives the sentence back.
        /// Non-speech tags ("[Music]", "(laughs)", "♪") are dropped. <paramref name="progress"/> gets 0..1.
        /// </summary>
        public static async Task<List<TimedWord>> TranscribeAsync(float[] samples, string modelPath, TranscribeOptions options,
            double offsetSec = 0, IProgress<double>? progress = null, CancellationToken cancel = default)
        {
            using var factory = WhisperFactory.FromPath(modelPath);
            var builder = factory.CreateBuilder()
                .WithLanguage(string.IsNullOrWhiteSpace(options.Language) ? "auto" : options.Language)
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 1, 8))
                // one segment a word, cut at word boundaries: whisper.cpp's way to time words
                .WithTokenTimestamps()
                .SplitOnWord()
                .WithMaxSegmentLength(1)
                .WithProgressHandler(percent => progress?.Report(percent / 100.0));
            if (options.TranslateToEnglish)
                builder = builder.WithTranslate();
            await using var processor = builder.Build();

            var words = new List<TimedWord>();
            char? closing = null; // inside "(upbeat music)" or "[applause]", which come a word at a time
            await foreach (var segment in processor.ProcessAsync(samples, cancel))
            {
                var text = segment.Text;
                var trimmed = text.Trim();
                if (closing is { } close)
                {
                    if (trimmed.EndsWith(close))
                        closing = null;
                    continue;
                }
                if (trimmed.Length > 0 && trimmed[0] is '(' or '[' && !trimmed.EndsWith(trimmed[0] == '(' ? ')' : ']'))
                {
                    closing = trimmed[0] == '(' ? ')' : ']';
                    continue;
                }
                if (trimmed.Length == 0 || IsTag(trimmed))
                    continue;
                var start = offsetSec + segment.Start.TotalSeconds;
                var end = Math.Max(start, offsetSec + segment.End.TotalSeconds);
                // a lone punctuation mark belongs to the word before it
                if (words.Count > 0 && trimmed.All(char.IsPunctuation))
                {
                    var last = words[^1];
                    words[^1] = last with { Text = last.Text + trimmed, EndSec = Math.Max(last.EndSec, end) };
                    continue;
                }
                words.Add(new TimedWord(text, start, end));
            }
            return words;
        }

        /// <summary>A sound description rather than speech: "[BLANK_AUDIO]", "(music)", "♪♪".</summary>
        private static bool IsTag(string t)
        {
            return (t.StartsWith('[') && t.EndsWith(']')) || (t.StartsWith('(') && t.EndsWith(')'))
                || t.All(c => c is '♪' or '♫' or '*' or ' ' or '-');
        }
    }
}
