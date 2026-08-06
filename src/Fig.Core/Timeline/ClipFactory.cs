using System;
using System.Collections.Generic;

namespace Fig.Core.Timeline
{
    public static class ClipFactory
    {
        public static Clip Clone(Clip source)
        {
            Clip clone = source.Kind switch
            {
                ClipKind.Video => new VideoClip
                {
                    SourceId = ((VideoClip)source).SourceId,
                    SrcInSec = ((VideoClip)source).SrcInSec,
                    SrcOutSec = ((VideoClip)source).SrcOutSec,
                    CropL = ((VideoClip)source).CropL,
                    CropT = ((VideoClip)source).CropT,
                    CropR = ((VideoClip)source).CropR,
                    CropB = ((VideoClip)source).CropB,
                    StartSec = source.StartSec,
                    DurSec = source.DurSec,
                    Speed = source.Speed,
                    Volume = source.Volume,
                    Opacity = source.Opacity,
                    FadeInSec = source.FadeInSec,
                    FadeOutSec = source.FadeOutSec,
                },
                ClipKind.Audio => new AudioClip
                {
                    SourceId = ((AudioClip)source).SourceId,
                    SrcInSec = ((AudioClip)source).SrcInSec,
                    SrcOutSec = ((AudioClip)source).SrcOutSec,
                    StartSec = source.StartSec,
                    DurSec = source.DurSec,
                    Speed = source.Speed,
                    Volume = source.Volume,
                    Opacity = source.Opacity,
                    FadeInSec = source.FadeInSec,
                    FadeOutSec = source.FadeOutSec,
                },
                ClipKind.Text => new TextClip
                {
                    Text = ((TextClip)source).Text,
                    Font = ((TextClip)source).Font,
                    Size = ((TextClip)source).Size,
                    Color = ((TextClip)source).Color,
                    StartSec = source.StartSec,
                    DurSec = source.DurSec,
                    Speed = source.Speed,
                    Volume = source.Volume,
                    Opacity = source.Opacity,
                    FadeInSec = source.FadeInSec,
                    FadeOutSec = source.FadeOutSec,
                },
                _ => throw new NotSupportedException($"Unsupported clip kind '{source.Kind}'")
            };
            clone.LinkGroupId = source.LinkGroupId;
            clone.Enabled = source.Enabled;
            clone.Effects = CloneEffects(source.Effects);
            clone.TransitionIn = source.TransitionIn?.Clone();
            clone.TransitionOut = source.TransitionOut?.Clone();
            clone.Markers = CloneMarkers(source.Markers);
            clone.Metadata = source.Metadata.Count == 0
                ? new Dictionary<string, System.Text.Json.JsonElement>()
                : new Dictionary<string, System.Text.Json.JsonElement>(source.Metadata);
            return clone;
        }

        private static List<Marker> CloneMarkers(List<Marker> source)
        {
            var list = new List<Marker>(source.Count);
            foreach (var m in source)
                list.Add(m.Clone());
            return list;
        }

        private static List<EffectInstance> CloneEffects(List<EffectInstance> source)
        {
            var list = new List<EffectInstance>(source.Count);
            foreach (var e in source)
                list.Add(e.Clone());
            return list;
        }

        public static Clip CloneWithRange(Clip source, double startSec, double durSec, double srcInSec, double srcOutSec)
        {
            var clone = Clone(source);
            clone.StartSec = startSec;
            clone.DurSec = durSec;
            SetSourceRange(clone, srcInSec, srcOutSec);
            return clone;
        }

        public static void SetSourceRange(Clip clip, double srcInSec, double srcOutSec)
        {
            switch (clip)
            {
                case VideoClip v:
                    v.SrcInSec = srcInSec;
                    v.SrcOutSec = srcOutSec;
                    break;
                case AudioClip a:
                    a.SrcInSec = srcInSec;
                    a.SrcOutSec = srcOutSec;
                    break;
            }
        }
    }
}
