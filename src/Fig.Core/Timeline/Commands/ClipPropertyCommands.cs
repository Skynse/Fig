using System;
using System.Collections.Generic;
using System.Linq;

namespace Fig.Core.Timeline
{
    /// <summary>
    /// Commands that can merge with a subsequent edit of the same property on the same
    /// target (e.g. scrubbing an opacity slider produces one undo step).
    /// </summary>
    public interface ICoalescingEditCommand : IEditCommand
    {
        bool CanCoalesceWith(IEditCommand other);
        void CoalesceFrom(IEditCommand other);
    }

    /// <summary>Sets fade-in duration on a clip and its link group (video opacity + audio volume).</summary>
    public sealed class SetFadeInCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly Dictionary<string, double> _old = new();
        private readonly FadeOf _of;
        private double _newValue;

        public string Description => "Set fade in";

        public SetFadeInCommand(TimelineEditor editor, string clipId, double newValue, FadeOf of = FadeOf.Picture)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = Math.Max(0, newValue);
            _of = of;
        }

        public void Execute()
        {
            _old.Clear();
            foreach (var clip in Targets())
            {
                _old[clip.Id] = ClipFade.Get(clip, _of).In;
                ClipFade.ApplyFadeIn(clip, _newValue, _of);
            }
            if (_old.Count == 0)
                throw new InvalidOperationException($"Clip '{_clipId}' not found");
        }

        public void Undo()
        {
            foreach (var (id, value) in _old)
            {
                var clip = _editor.FindClip(id);
                if (clip is not null)
                {
                    var (fadeIn, fadeOut) = ClipFade.Get(clip, _of);
                    ClipFade.Set(clip, _of, value, fadeOut);
                }
            }
        }

        public void Redo() => ApplyNew();

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetFadeInCommand o && o._clipId == _clipId && o._of == _of;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetFadeInCommand o)
            {
                _newValue = o._newValue;
                ApplyNew();
            }
        }

        private void ApplyNew()
        {
            foreach (var id in _old.Keys)
            {
                var clip = _editor.FindClip(id);
                if (clip is not null)
                    ClipFade.ApplyFadeIn(clip, _newValue, _of);
            }
        }

        private IEnumerable<Clip> Targets()
            => _editor.LinkGroup(_clipId);
    }

    /// <summary>Sets fade-out duration on a clip and its link group (video opacity + audio volume).</summary>
    public sealed class SetFadeOutCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly Dictionary<string, double> _old = new();
        private readonly FadeOf _of;
        private double _newValue;

        public string Description => "Set fade out";

        public SetFadeOutCommand(TimelineEditor editor, string clipId, double newValue, FadeOf of = FadeOf.Picture)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = Math.Max(0, newValue);
            _of = of;
        }

        public void Execute()
        {
            _old.Clear();
            foreach (var clip in Targets())
            {
                _old[clip.Id] = ClipFade.Get(clip, _of).Out;
                ClipFade.ApplyFadeOut(clip, _newValue, _of);
            }
            if (_old.Count == 0)
                throw new InvalidOperationException($"Clip '{_clipId}' not found");
        }

        public void Undo()
        {
            foreach (var (id, value) in _old)
            {
                var clip = _editor.FindClip(id);
                if (clip is not null)
                {
                    var (fadeIn, fadeOut) = ClipFade.Get(clip, _of);
                    ClipFade.Set(clip, _of, fadeIn, value);
                }
            }
        }

        public void Redo() => ApplyNew();

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetFadeOutCommand o && o._clipId == _clipId && o._of == _of;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetFadeOutCommand o)
            {
                _newValue = o._newValue;
                ApplyNew();
            }
        }

        private void ApplyNew()
        {
            foreach (var id in _old.Keys)
            {
                var clip = _editor.FindClip(id);
                if (clip is not null)
                    ClipFade.ApplyFadeOut(clip, _newValue, _of);
            }
        }

        private IEnumerable<Clip> Targets()
            => _editor.LinkGroup(_clipId);
    }

    /// <summary>
    /// Sets playback speed on a clip (and its link group). The source span stays fixed, so the
    /// clip's timeline duration recomputes to <c>sourceSpan / speed</c> — a 2x clip becomes half
    /// as long on the timeline. Changing the duration can push the clip's end past the next clip,
    /// which would leave overlapping clips and break timeline hit-testing, so every later clip on
    /// the same track is pushed out of the way by the same delta. Undo restores both the clip and
    /// the pushed clips.
    /// </summary>
    public sealed class SetSpeedCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly Dictionary<string, (double Speed, double DurSec)> _old = new();
        private readonly List<(Clip Clip, double OldStart)> _shifted = new();
        private double _newValue;

        public string Description => "Set speed";

        public SetSpeedCommand(TimelineEditor editor, string clipId, double newValue)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = Math.Clamp(newValue, TimelineEditor.MinSpeed, TimelineEditor.MaxSpeed);
        }

        public void Execute()
        {
            _old.Clear();
            _shifted.Clear();

            var targets = new List<Clip>();
            foreach (var clip in Targets())
            {
                _old[clip.Id] = (clip.Speed, clip.DurSec);
                ApplySpeed(clip);
                targets.Add(clip);
            }
            if (_old.Count == 0)
                throw new InvalidOperationException($"Clip '{_clipId}' not found");

            // Push later clips on each affected track out of the way so nothing overlaps.
            foreach (var track in _editor.Document.Tracks)
            {
                var onTrack = targets.Where(t => ReferenceEquals(_editor.FindClipTrack(t.Id), track)).ToList();
                if (onTrack.Count == 0)
                    continue;

                var oldEnd = onTrack.Max(t => t.StartSec + _old[t.Id].DurSec);
                var newEnd = onTrack.Max(t => t.StartSec + t.DurSec);
                var delta = newEnd - oldEnd;
                if (Math.Abs(delta) < 1e-9)
                    continue;

                var affected = onTrack.Select(t => t.Id).ToHashSet();
                foreach (var clip in track.Clips)
                {
                    if (affected.Contains(clip.Id))
                        continue;
                    if (clip.StartSec >= oldEnd - 1e-9)
                    {
                        _shifted.Add((clip, clip.StartSec));
                        clip.StartSec += delta;
                    }
                }
            }
        }

        public void Undo()
        {
            foreach (var (id, value) in _old)
            {
                var clip = _editor.FindClip(id);
                if (clip is not null)
                {
                    clip.Speed = value.Speed;
                    clip.DurSec = value.DurSec;
                }
            }
            foreach (var (clip, oldStart) in _shifted)
                clip.StartSec = oldStart;
        }

        public void Redo() => Execute();

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetSpeedCommand o && o._clipId == _clipId;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetSpeedCommand o)
            {
                _newValue = o._newValue;
                // Re-run from the pre-operation state. Slider drags coalesce into this one
                // command; without restoring first, the later clips would be pushed twice.
                Undo();
                Execute();
            }
        }

        private void ApplySpeed(Clip clip)
        {
            var oldSpeed = clip.Speed;
            clip.Speed = _newValue;
            double span = 0;
            if (clip.HasSourceRange)
                span = (clip.SourceOut > 0 ? clip.SourceOut : clip.SourceIn + clip.DurSec * oldSpeed) - clip.SourceIn;
            if (span > 1e-9)
                clip.DurSec = span / _newValue;
        }

        private IEnumerable<Clip> Targets()
        {
            foreach (var clip in _editor.LinkGroup(_clipId))
            {
                if (clip.Facets.HasFlag(Facet.Retime))
                    yield return clip;
            }
        }
    }
}
