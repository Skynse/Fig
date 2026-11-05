using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Fig.Core.Timeline;

namespace Fig.Core.Captions
{
    /// <summary>One caption: what it says, when it shows on the timeline, and its words (timed from the timeline's start).</summary>
    public sealed record CaptionCue(double StartSec, double EndSec, string Text, IReadOnlyList<TimedWord> Words);

    /// <summary>How words are grouped into captions.</summary>
    public sealed record CaptionGrouping(int MaxWords = 4, int MaxLineChars = 24, int MaxLines = 2, double MaxPauseSec = 0.6)
    {
        /// <summary>The choices the captions dialog offers, quickest first.</summary>
        public static IReadOnlyList<(string Name, CaptionGrouping Grouping)> Choices { get; } =
        [
            ("One word at a time", new CaptionGrouping(MaxWords: 1, MaxLines: 1)),
            ("A few words", new CaptionGrouping(MaxWords: 3, MaxLines: 1)),
            ("Short phrases", new CaptionGrouping(MaxWords: 6)),
            ("Full lines", new CaptionGrouping(MaxWords: 14, MaxLineChars: 36)),
        ];
    }

    /// <summary>Turns timed words into captions, and captions into subtitle files.</summary>
    public static class CaptionBuilder
    {
        /// <summary>A caption that would otherwise blink off for less than this before the next one stays up until it.</summary>
        private const double HoldGapSec = 0.5;

        /// <summary>
        /// Groups words (timed from the timeline's start, in order) into captions: a caption ends at a sentence end, a
        /// pause, or when it is full, and a long one is broken over lines at the word that balances them best.
        /// Each caption lasts until its last word ends, or until the next one when the gap is short; none overlap.
        /// </summary>
        public static List<CaptionCue> Group(IReadOnlyList<TimedWord> words, CaptionGrouping grouping)
        {
            var maxChars = grouping.MaxLineChars * Math.Max(1, grouping.MaxLines);
            var groups = new List<List<TimedWord>>();
            var current = new List<TimedWord>();
            foreach (var word in words)
            {
                if (current.Count > 0)
                {
                    var last = current[^1];
                    var full = current.Count >= grouping.MaxWords
                        || Join(current).Length + 1 + word.Text.Trim().Length > maxChars;
                    if (full || EndsSentence(last.Text) || word.StartSec - last.EndSec > grouping.MaxPauseSec)
                    {
                        groups.Add(current);
                        current = new List<TimedWord>();
                    }
                }
                current.Add(word);
            }
            if (current.Count > 0)
                groups.Add(current);

            var cues = new List<CaptionCue>(groups.Count);
            for (var i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var start = g[0].StartSec;
                var end = Math.Max(g[^1].EndSec, start + 0.1);
                if (i + 1 < groups.Count)
                {
                    var next = groups[i + 1][0].StartSec;
                    end = next - end < HoldGapSec ? next : Math.Min(end, next);
                }
                cues.Add(new CaptionCue(start, end, Lines(g, grouping), g));
            }
            return cues;
        }

        /// <summary>The words as one line, spaced as they were said (no spaces are added where the language has none).</summary>
        private static string Join(IEnumerable<TimedWord> words) => string.Concat(words.Select(w => w.Text)).Trim();

        /// <summary>The caption's text, broken into two lines when it is longer than one line holds.</summary>
        private static string Lines(List<TimedWord> words, CaptionGrouping grouping)
        {
            var text = Join(words);
            if (grouping.MaxLines < 2 || text.Length <= grouping.MaxLineChars || words.Count < 2)
                return text;
            // the break that leaves the two lines closest in length
            var best = 1;
            var bestDiff = int.MaxValue;
            for (var k = 1; k < words.Count; k++)
            {
                var diff = Math.Abs(Join(words.Take(k)).Length - Join(words.Skip(k)).Length);
                if (diff < bestDiff)
                    (best, bestDiff) = (k, diff);
            }
            return Join(words.Take(best)) + "\n" + Join(words.Skip(best));
        }

        private static bool EndsSentence(string word)
        {
            var w = word.TrimEnd('"', '\'', '”', '’', ')', ' ');
            return w.Length > 0 && w[^1] is '.' or '!' or '?' or '…' or '。' or '！' or '？';
        }

        /// <summary>SubRip: numbered cues, <c>hh:mm:ss,mmm --&gt; hh:mm:ss,mmm</c>, the text, a blank line.</summary>
        public static string ToSrt(IEnumerable<CaptionCue> cues)
        {
            var sb = new StringBuilder();
            var n = 1;
            foreach (var cue in cues)
            {
                sb.Append(n++).Append('\n');
                sb.Append(SrtTime(cue.StartSec)).Append(" --> ").Append(SrtTime(cue.EndSec)).Append('\n');
                sb.Append(cue.Text.Replace("\r\n", "\n").Trim()).Append("\n\n");
            }
            return sb.ToString();
        }

        /// <summary>The transcript: one caption a line, without times.</summary>
        public static string ToPlainText(IEnumerable<CaptionCue> cues)
            => string.Concat(cues.Select(c => c.Text.Replace("\r\n", " ").Replace('\n', ' ').Trim() + "\n"));

        /// <summary><c>hh:mm:ss,mmm</c>, rounded to the millisecond.</summary>
        public static string SrtTime(double seconds)
        {
            var ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
            return string.Create(CultureInfo.InvariantCulture,
                $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00},{ms % 1000:000}");
        }
    }
}
