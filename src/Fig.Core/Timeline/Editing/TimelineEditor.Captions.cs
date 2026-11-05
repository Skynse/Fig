using System;
using System.Collections.Generic;
using System.Linq;
using Fig.Core.Captions;

namespace Fig.Core.Timeline
{
    public partial class TimelineEditor
    {
        /// <summary>Where generated captions sit: across the middle, low enough to clear the subject, high enough to clear a phone app's buttons.</summary>
        public static readonly LayerTransform CaptionLayout = LayerTransform.Default with { CenterY = 0.72 };

        /// <summary>
        /// Puts captions on a new video track on top of the picture, one text clip each, as one undo step. Times are
        /// snapped to frames and the clips never overlap. Returns the new track, or null when there was nothing to add.
        /// </summary>
        public Track? AddCaptions(IReadOnlyList<CaptionCue> cues, TextStyle? style = null, LayerTransform? layout = null)
        {
            var rate = Document.Rate;
            var frame = 1.0 / rate.Fps;
            var track = new Track { Kind = TrackKind.Video, Name = "Captions" };
            var lastEnd = 0.0;
            foreach (var cue in cues.OrderBy(c => c.StartSec))
            {
                var start = Math.Max(lastEnd, FrameMath.SnapToFrame(Math.Max(0, cue.StartSec), rate));
                var end = FrameMath.SnapToFrame(cue.EndSec, rate);
                if (end - start < frame - 1e-9)
                    end = start + frame;
                var clip = new TextClip
                {
                    Text = cue.Text,
                    IsCaption = true,
                    Style = style ?? TextStyle.Captions,
                    Layout = layout ?? CaptionLayout,
                    StartSec = start,
                    DurSec = end - start,
                    Words = cue.Words.Select(w => new TimedWord(w.Text.Trim(), w.StartSec - start, w.EndSec - start)).ToList(),
                };
                track.Clips.Add(clip);
                lastEnd = end;
            }
            if (track.Clips.Count == 0)
                return null;

            var before = Document.Tracks.ToList();
            History.Execute(new SetTrackListCommand(this, "Add captions", before, WithNewTrack(before, track)));
            RefreshTrackIndices();
            Selection.SelectOnly(track.Clips[0].Id);
            RaiseChanged();
            return track;
        }

        /// <summary>
        /// The timeline's captions (the enabled caption text clips on visible tracks), in order, for a subtitle file.
        /// Times are clamped to <paramref name="startSec"/>..<paramref name="endSec"/> and measured from
        /// <paramref name="startSec"/>, so a caption file matches a video exported from that range.
        /// </summary>
        public List<CaptionCue> CaptionCues(double startSec = 0, double endSec = double.PositiveInfinity)
            => Document.Tracks
                .Where(t => t is { Kind: TrackKind.Video, Visible: true })
                .SelectMany(t => t.Clips)
                .OfType<TextClip>()
                .Where(c => c.IsCaption && c.Enabled && !string.IsNullOrWhiteSpace(c.Text)
                    && c.StartSec < endSec && c.StartSec + c.DurSec > startSec)
                .OrderBy(c => c.StartSec)
                .Select(c => new CaptionCue(
                    Math.Max(c.StartSec, startSec) - startSec,
                    Math.Min(c.StartSec + c.DurSec, endSec) - startSec,
                    TextWords.Display(c),
                    TextWords.Shift(c.Words, startSec - c.StartSec)))
                .ToList();

        /// <summary>
        /// The listed clips and, for each caption among them, every caption on its track: what a style change reaches
        /// when it is set for all captions.
        /// </summary>
        public IReadOnlyList<string> WithCaptionTrackmates(IEnumerable<string> clipIds)
        {
            var ids = clipIds.ToList();
            var tracks = ids.Where(id => FindClip(id) is TextClip { IsCaption: true })
                .Select(FindClipTrack).OfType<Track>().Distinct();
            return ids.Concat(tracks.SelectMany(CaptionsOn).Select(c => c.Id)).Distinct().ToList();
        }

        /// <summary>The captions on a track, in order.</summary>
        public static IEnumerable<TextClip> CaptionsOn(Track track)
            => track.Clips.OfType<TextClip>().Where(c => c.IsCaption).OrderBy(c => c.StartSec);

        /// <summary>
        /// Rewrites what text clips say, as one undo step (typing coalesces into it). A caption's words are retimed
        /// (<see cref="TextWords.Retime"/>), so the spoken word stays lit through the edit.
        /// </summary>
        public void SetText(IEnumerable<string> clipIds, string text)
        {
            var values = clipIds.Distinct().Select(FindClip).OfType<TextClip>().Where(c => c.Text != text)
                .ToDictionary(c => c.Id, c => (text, TextWords.Retime(c.Words, text)));
            if (values.Count == 0)
                return;
            History.ExecuteCoalescing(new SetClipValueCommand<(string, IReadOnlyList<TimedWord>)>(this, values, "text", GetWords, SetWords));
            RaiseChanged();
        }

        private static (string, IReadOnlyList<TimedWord>) GetWords(Clip c) => (((TextClip)c).Text, ((TextClip)c).Words);

        private static void SetWords(Clip c, (string Text, IReadOnlyList<TimedWord> Words) v)
        {
            ((TextClip)c).Text = v.Text;
            ((TextClip)c).Words = v.Words;
        }

        /// <summary>
        /// Replaces <paramref name="find"/> with <paramref name="replacement"/> in every caption (on
        /// <paramref name="trackId"/>, or on any track), as one undo step. Returns how many were replaced.
        /// </summary>
        public int ReplaceInCaptions(string find, string replacement, bool matchCase, string? trackId = null)
        {
            if (find.Length == 0)
                return 0;
            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var count = 0;
            var values = new Dictionary<string, (string, IReadOnlyList<TimedWord>)>();
            foreach (var track in Document.Tracks.Where(t => trackId is null || t.Id == trackId))
                foreach (var clip in CaptionsOn(track))
                {
                    var n = Occurrences(clip.Text, find, comparison);
                    if (n == 0)
                        continue;
                    var text = clip.Text.Replace(find, replacement, comparison);
                    values[clip.Id] = (text, TextWords.Retime(clip.Words, text));
                    count += n;
                }
            if (count == 0)
                return 0;
            History.BreakCoalescing();
            History.Execute(new SetClipValueCommand<(string, IReadOnlyList<TimedWord>)>(this, values, "replace", GetWords, SetWords));
            RaiseChanged();
            return count;
        }

        private static int Occurrences(string text, string find, StringComparison comparison)
        {
            var n = 0;
            for (var i = text.IndexOf(find, comparison); i >= 0; i = text.IndexOf(find, i + find.Length, comparison))
                n++;
            return n;
        }

        /// <summary>
        /// Joins a caption with the next one on its track: one caption from the first's start to the second's end,
        /// saying both, with both sets of word timings. One undo step. Returns the joined caption, or null when there
        /// is no next caption.
        /// </summary>
        public TextClip? MergeCaptionWithNext(string clipId)
        {
            if (FindClip(clipId) is not TextClip { IsCaption: true } first || FindClipTrack(clipId) is not { } track)
                return null;
            var next = CaptionsOn(track).FirstOrDefault(c => c.StartSec > first.StartSec);
            if (next is null)
                return null;

            var merged = (TextClip)first.Clone();
            merged.Id = first.Id;
            merged.Text = Flat(first.Text) + " " + Flat(next.Text);
            merged.DurSec = next.StartSec + next.DurSec - first.StartSec;
            merged.Words = first.Words.Count > 0 && next.Words.Count > 0
                ? first.Words.Concat(TextWords.Shift(next.Words, first.StartSec - next.StartSec)).ToList()
                : TextWords.Retime(first.Words, merged.Text);
            merged.FadeOutSec = next.FadeOutSec;
            merged.TransitionOut = next.TransitionOut?.Clone();
            History.Execute(new ReplaceClipsCommand(this, track.Id, "Merge captions", [first, next], [merged]));
            Selection.SelectOnly(merged.Id);
            RaiseChanged();
            return merged;
        }

        private static string Flat(string text) => string.Join(' ', text.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        /// <summary>
        /// Splits a caption in two before the word at character <paramref name="charIndex"/> of its text (a split inside
        /// a word moves to that word's start). The cut falls where that word is said, or in proportion to the text when
        /// the caption has no word timings. One undo step. Returns the two halves, or null when there is nothing on one side.
        /// </summary>
        public (TextClip Left, TextClip Right)? SplitCaption(string clipId, int charIndex)
        {
            if (FindClip(clipId) is not TextClip { IsCaption: true } caption || FindClipTrack(clipId) is not { } track)
                return null;
            var spans = TextWords.Spans(caption.Text);
            // the first word that starts at or after the cut, or that the cut falls inside
            var k = spans.FindIndex(s => s.Start + s.Length > charIndex);
            if (k <= 0)
                return null;
            var at = spans[k].Start;
            var leftText = caption.Text[..at].Trim();
            var rightText = caption.Text[at..].Trim();

            var timed = caption.Words.Count == spans.Count;
            var cut = timed
                ? caption.Words[k].StartSec
                : caption.DurSec * caption.Text[..at].Length / Math.Max(1, caption.Text.Length);
            var frame = 1.0 / Document.Rate.Fps;
            cut = Math.Clamp(FrameMath.SnapToFrame(caption.StartSec + cut, Document.Rate) - caption.StartSec, frame, caption.DurSec - frame);
            if (cut < frame - 1e-9 || caption.DurSec - cut < frame - 1e-9)
                return null;

            var left = (TextClip)caption.Clone();
            left.Id = caption.Id;
            left.Text = leftText;
            left.DurSec = cut;
            left.FadeOutSec = 0;
            left.TransitionOut = null;
            left.Words = timed ? caption.Words.Take(k).ToList() : [];
            ClipTime.KeepBefore(left, cut);

            var right = (TextClip)caption.Clone();
            right.Text = rightText;
            right.StartSec = caption.StartSec + cut;
            right.DurSec = caption.DurSec - cut;
            right.FadeInSec = 0;
            right.TransitionIn = null;
            ClipTime.KeepAfter(right, cut);
            right.Words = timed ? caption.Words.Skip(k).Select(w => w with { StartSec = w.StartSec - cut, EndSec = w.EndSec - cut }).ToList() : [];

            History.Execute(new ReplaceClipsCommand(this, track.Id, "Split caption", [caption], [left, right]));
            Selection.SelectOnly(right.Id);
            RaiseChanged();
            return (left, right);
        }

        /// <summary>Takes a caption off its track. One undo step.</summary>
        public void RemoveCaption(string clipId)
        {
            if (FindClip(clipId) is not TextClip { IsCaption: true } caption || FindClipTrack(clipId) is not { } track)
                return;
            History.Execute(new ReplaceClipsCommand(this, track.Id, "Delete caption", [caption], []));
            Selection.Clear();
            RaiseChanged();
        }

        /// <summary>True when the timeline has any caption to export.</summary>
        public bool HasCaptions => Document.Tracks.SelectMany(t => t.Clips).Any(c => c is TextClip { IsCaption: true });
    }
}
