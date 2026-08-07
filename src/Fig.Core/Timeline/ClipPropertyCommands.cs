using System;
using System.Collections.Generic;

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

    /// <summary>Sets opacity on one clip (and linked video clips) with undo coalescing.</summary>
    public sealed class SetOpacityCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly Dictionary<string, double> _old = new();
        private double _newValue;

        public string Description => "Set opacity";
        public string ClipId => _clipId;
        public double NewValue => _newValue;

        public SetOpacityCommand(TimelineEditor editor, string clipId, double newValue)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = Math.Clamp(newValue, 0, 1);
        }

        public void Execute()
        {
            _old.Clear();
            foreach (var clip in Targets())
            {
                _old[clip.Id] = clip.Opacity;
                clip.Opacity = _newValue;
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
                    clip.Opacity = value;
            }
        }

        public void Redo() => ApplyNew();

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetOpacityCommand o && o._clipId == _clipId;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetOpacityCommand o)
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
                    clip.Opacity = _newValue;
            }
        }

        private IEnumerable<Clip> Targets()
        {
            // opacity is visual — apply to the selected clip and any linked video peers
            foreach (var clip in _editor.LinkGroup(_clipId))
            {
                if (clip is VideoClip || clip.Id == _clipId)
                    yield return clip;
            }
        }
    }

    /// <summary>Sets fade-in duration on a clip and its link group (video opacity + audio volume).</summary>
    public sealed class SetFadeInCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly Dictionary<string, double> _old = new();
        private double _newValue;

        public string Description => "Set fade in";

        public SetFadeInCommand(TimelineEditor editor, string clipId, double newValue)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = Math.Max(0, newValue);
        }

        public void Execute()
        {
            _old.Clear();
            foreach (var clip in Targets())
            {
                _old[clip.Id] = clip.FadeInSec;
                ClipFade.ApplyFadeIn(clip, _newValue);
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
                    clip.FadeInSec = value;
            }
        }

        public void Redo() => ApplyNew();

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetFadeInCommand o && o._clipId == _clipId;

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
                    ClipFade.ApplyFadeIn(clip, _newValue);
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
        private double _newValue;

        public string Description => "Set fade out";

        public SetFadeOutCommand(TimelineEditor editor, string clipId, double newValue)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = Math.Max(0, newValue);
        }

        public void Execute()
        {
            _old.Clear();
            foreach (var clip in Targets())
            {
                _old[clip.Id] = clip.FadeOutSec;
                ClipFade.ApplyFadeOut(clip, _newValue);
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
                    clip.FadeOutSec = value;
            }
        }

        public void Redo() => ApplyNew();

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetFadeOutCommand o && o._clipId == _clipId;

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
                    ClipFade.ApplyFadeOut(clip, _newValue);
            }
        }

        private IEnumerable<Clip> Targets()
            => _editor.LinkGroup(_clipId);
    }

    /// <summary>Sets volume on one clip (and linked audio clips) with undo coalescing.</summary>
    public sealed class SetVolumeCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly Dictionary<string, double> _old = new();
        private double _newValue;

        public string Description => "Set volume";

        public SetVolumeCommand(TimelineEditor editor, string clipId, double newValue)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = Math.Clamp(newValue, 0, 1);
        }

        public void Execute()
        {
            _old.Clear();
            foreach (var clip in Targets())
            {
                _old[clip.Id] = clip.Volume;
                clip.Volume = _newValue;
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
                    clip.Volume = value;
            }
        }

        public void Redo() => ApplyNew();

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetVolumeCommand o && o._clipId == _clipId;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetVolumeCommand o)
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
                    clip.Volume = _newValue;
            }
        }

        private IEnumerable<Clip> Targets()
        {
            foreach (var clip in _editor.LinkGroup(_clipId))
            {
                if (clip is AudioClip || clip.Id == _clipId)
                    yield return clip;
            }
        }
    }

    /// <summary>Sets normalized crop insets on a video clip with undo coalescing.</summary>
    public sealed class SetCropCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private double _oldL, _oldT, _oldR, _oldB;
        private double _newL, _newT, _newR, _newB;
        private bool _hasOld;

        public string Description => "Set crop";

        public SetCropCommand(TimelineEditor editor, string clipId,
            double cropL, double cropT, double cropR, double cropB)
        {
            _editor = editor;
            _clipId = clipId;
            (_newL, _newT, _newR, _newB) = Normalize(cropL, cropT, cropR, cropB);
        }

        public void Execute()
        {
            var clip = _editor.FindClip(_clipId) as VideoClip
                ?? throw new InvalidOperationException($"Video clip '{_clipId}' not found");
            _oldL = clip.CropL;
            _oldT = clip.CropT;
            _oldR = clip.CropR;
            _oldB = clip.CropB;
            _hasOld = true;
            Apply(_newL, _newT, _newR, _newB);
        }

        public void Undo()
        {
            if (_hasOld)
                Apply(_oldL, _oldT, _oldR, _oldB);
        }

        public void Redo() => Apply(_newL, _newT, _newR, _newB);

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetCropCommand o && o._clipId == _clipId;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetCropCommand o)
            {
                _newL = o._newL;
                _newT = o._newT;
                _newR = o._newR;
                _newB = o._newB;
                Apply(_newL, _newT, _newR, _newB);
            }
        }

        private void Apply(double l, double t, double r, double b)
        {
            if (_editor.FindClip(_clipId) is not VideoClip clip)
                return;
            clip.CropL = l;
            clip.CropT = t;
            clip.CropR = r;
            clip.CropB = b;
        }

        /// <summary>Clamp insets and ensure at least a 5% window remains.</summary>
        public static (double L, double T, double R, double B) Normalize(double l, double t, double r, double b)
        {
            l = Math.Clamp(l, 0, 0.45);
            t = Math.Clamp(t, 0, 0.45);
            r = Math.Clamp(r, 0, 0.45);
            b = Math.Clamp(b, 0, 0.45);
            if (l + r > 0.9)
            {
                var scale = 0.9 / (l + r);
                l *= scale;
                r *= scale;
            }
            if (t + b > 0.9)
            {
                var scale = 0.9 / (t + b);
                t *= scale;
                b *= scale;
            }
            return (l, t, r, b);
        }
    }

    /// <summary>
    /// Sets playback speed on a clip (and its link group). The source span stays fixed, so the
    /// clip's timeline duration recomputes to <c>sourceSpan / speed</c> — a 2x clip becomes half
    /// as long on the timeline. Undo restores both speed and duration.
    /// </summary>
    public sealed class SetSpeedCommand : ICoalescingEditCommand
    {
        private readonly TimelineEditor _editor;
        private readonly string _clipId;
        private readonly Dictionary<string, (double Speed, double DurSec)> _old = new();
        private double _newValue;

        public string Description => "Set speed";

        public SetSpeedCommand(TimelineEditor editor, string clipId, double newValue)
        {
            _editor = editor;
            _clipId = clipId;
            _newValue = Math.Clamp(newValue, 0.1, 8.0);
        }

        public void Execute()
        {
            _old.Clear();
            foreach (var clip in Targets())
            {
                _old[clip.Id] = (clip.Speed, clip.DurSec);
                ApplySpeed(clip);
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
                    clip.Speed = value.Speed;
                    clip.DurSec = value.DurSec;
                }
            }
        }

        public void Redo()
        {
            foreach (var id in _old.Keys)
            {
                var clip = _editor.FindClip(id);
                if (clip is not null)
                    ApplySpeed(clip);
            }
        }

        public bool CanCoalesceWith(IEditCommand other)
            => other is SetSpeedCommand o && o._clipId == _clipId;

        public void CoalesceFrom(IEditCommand other)
        {
            if (other is SetSpeedCommand o)
            {
                _newValue = o._newValue;
                Redo();
            }
        }

        private void ApplySpeed(Clip clip)
        {
            var oldSpeed = clip.Speed;
            clip.Speed = _newValue;
            double span = 0;
            if (clip is VideoClip vc)
                span = (vc.SrcOutSec > 0 ? vc.SrcOutSec : vc.SrcInSec + clip.DurSec * oldSpeed) - vc.SrcInSec;
            else if (clip is AudioClip ac)
                span = (ac.SrcOutSec > 0 ? ac.SrcOutSec : ac.SrcInSec + clip.DurSec * oldSpeed) - ac.SrcInSec;
            if (span > 1e-9)
                clip.DurSec = span / _newValue;
        }

        private IEnumerable<Clip> Targets()
        {
            foreach (var clip in _editor.LinkGroup(_clipId))
            {
                if (clip is VideoClip || clip is AudioClip)
                    yield return clip;
            }
        }
    }
}
