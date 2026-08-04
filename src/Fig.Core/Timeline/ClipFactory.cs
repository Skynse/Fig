using System;
using System.Collections.Generic;

namespace Fig.Core.Timeline
{
    public static class ClipFactory
    {
        public static Clip Clone(Clip source)
        {
            return source.Kind switch
            {
                ClipKind.Video => new VideoClip
                {
                    SourceId = ((VideoClip)source).SourceId,
                    SrcInSec = ((VideoClip)source).SrcInSec,
                    SrcOutSec = ((VideoClip)source).SrcOutSec,
                    StartSec = source.StartSec,
                    DurSec = source.DurSec,
                    Speed = source.Speed,
                    Volume = source.Volume,
                    Opacity = source.Opacity,
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
                },
                _ => throw new NotSupportedException($"Unsupported clip kind '{source.Kind}'")
            };
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
