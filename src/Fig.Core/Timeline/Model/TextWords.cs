using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// What a text clip shows and which of its words is being said. The words of the text are its
    /// whitespace-separated pieces; the i-th piece is the i-th timed word, as long as the counts agree. A typo fixed
    /// in the inspector keeps the timing; text rewritten into a different number of words loses the highlight.
    /// </summary>
    public static class TextWords
    {
        /// <summary>The text as drawn: line endings made <c>\n</c>, capitals when the style asks for them.</summary>
        public static string Display(TextClip clip) => Display(clip.Text, clip.Style);

        public static string Display(string text, TextStyle style)
        {
            var normal = text.Replace("\r\n", "\n");
            return style.Uppercase ? normal.ToUpperInvariant() : normal;
        }

        /// <summary>Where each word of <paramref name="text"/> is: its first character and its length.</summary>
        public static List<(int Start, int Length)> Spans(string text)
        {
            var spans = new List<(int, int)>();
            var start = -1;
            for (var i = 0; i <= text.Length; i++)
            {
                var space = i == text.Length || char.IsWhiteSpace(text[i]);
                if (!space && start < 0)
                    start = i;
                else if (space && start >= 0)
                {
                    spans.Add((start, i - start));
                    start = -1;
                }
            }
            return spans;
        }

        /// <summary>
        /// The word being said at <paramref name="localT"/> seconds into the clip, or -1: none (a pause before the
        /// first word, no timing, or a style without the highlight). Between two words the last one said stays lit.
        /// </summary>
        public static int ActiveIndex(TextClip clip, double localT)
        {
            var words = clip.Words;
            if (!clip.Style.Highlight || words.Count == 0 || localT < words[0].StartSec)
                return -1;
            if (Spans(Display(clip)).Count != words.Count)
                return -1;
            var index = 0;
            for (var i = 1; i < words.Count && words[i].StartSec <= localT; i++)
                index = i;
            return index;
        }

        /// <summary>The characters of the word being said at <paramref name="localT"/> in <see cref="Display(TextClip)"/>, or null.</summary>
        public static (int Start, int Length)? ActiveSpan(TextClip clip, double localT)
        {
            var index = ActiveIndex(clip, localT);
            return index < 0 ? null : Spans(Display(clip))[index];
        }

        /// <summary>
        /// Timed words for <paramref name="text"/>, a rewrite of the words <paramref name="old"/> timed: a word that is
        /// still there (matched in order, ignoring case and punctuation) keeps its time, and new words share the time
        /// between the kept ones around them by their length. A typo fixed without changing the count keeps every
        /// time. Typed text (no timed words) stays untimed.
        /// </summary>
        public static IReadOnlyList<TimedWord> Retime(IReadOnlyList<TimedWord> old, string text)
        {
            var spans = Spans(text);
            var tokens = spans.ConvertAll(s => text.Substring(s.Start, s.Length));
            if (old.Count == 0 || tokens.Count == 0)
                return [];
            var words = new TimedWord?[tokens.Count];
            if (tokens.Count == old.Count)
            {
                for (var i = 0; i < tokens.Count; i++)
                    words[i] = old[i] with { Text = tokens[i] };
                return words.Select(w => w!).ToList();
            }

            // the longest run of words kept in order (LCS), each kept word with its old time
            var a = old.Select(w => Key(w.Text)).ToArray();
            var b = tokens.Select(Key).ToArray();
            var lcs = new int[a.Length + 1, b.Length + 1];
            for (var i = a.Length - 1; i >= 0; i--)
                for (var j = b.Length - 1; j >= 0; j--)
                    lcs[i, j] = a[i] == b[j] && a[i].Length > 0 ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            for (int i = 0, j = 0; i < a.Length && j < b.Length;)
            {
                if (a[i] == b[j] && a[i].Length > 0)
                {
                    words[j] = old[i] with { Text = tokens[j] };
                    i++;
                    j++;
                }
                else if (lcs[i + 1, j] >= lcs[i, j + 1])
                    i++;
                else
                    j++;
            }

            // each run of new words shares the time between the kept words around it
            var (first, last) = (old[0].StartSec, old[^1].EndSec);
            for (var j = 0; j < words.Length;)
            {
                if (words[j] is not null)
                {
                    j++;
                    continue;
                }
                var k = j;
                while (k < words.Length && words[k] is null)
                    k++;
                var from = j > 0 ? words[j - 1]!.EndSec : first;
                var to = k < words.Length ? words[k]!.StartSec : last;
                if (to - from < MinWordSec * (k - j))
                {
                    // no room between them: share the kept word before (or after) as well
                    if (j > 0)
                        from = words[--j]!.StartSec;
                    else if (k < words.Length)
                        to = words[k++]!.EndSec;
                }
                to = Math.Max(to, from);
                var lengths = Enumerable.Range(j, k - j).Select(i => (double)Math.Max(1, tokens[i].Length)).ToList();
                var total = lengths.Sum();
                var at = from;
                for (var i = j; i < k; i++)
                {
                    var end = at + (to - from) * lengths[i - j] / total;
                    words[i] = new TimedWord(tokens[i], at, end);
                    at = end;
                }
                j = k;
            }
            return words.Select(w => w!).ToList();
        }

        /// <summary>The shortest time a new word is given before it borrows from its neighbours.</summary>
        private const double MinWordSec = 0.08;

        /// <summary>A word for matching: lower case, without punctuation.</summary>
        private static string Key(string word)
            => new(word.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

        /// <summary>
        /// Words moved <paramref name="offset"/> seconds earlier, for the right half of a cut. Words said before the cut
        /// stay (at negative times): the text still holds them, and each word must keep its place.
        /// </summary>
        public static IReadOnlyList<TimedWord> Shift(IReadOnlyList<TimedWord> words, double offset)
        {
            if (words.Count == 0 || offset == 0)
                return words;
            var shifted = new List<TimedWord>(words.Count);
            foreach (var w in words)
                shifted.Add(w with { StartSec = w.StartSec - offset, EndSec = w.EndSec - offset });
            return shifted;
        }
    }
}
