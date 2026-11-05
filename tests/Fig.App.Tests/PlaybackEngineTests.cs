using Fig.App.Services;
using Fig.Core.Audio;
namespace Fig.App.Tests;

public sealed class PlaybackEngineTests
{
    // 37.1s timeline with the playhead 3.18µs short of the end: a sub-half-frame gap used to spin the prebuffer forever
    [Fact] public void FramesLeft_SubHalfFrameGapIsTheEnd()=>Assert.Equal(0,PlaybackEngine.FramesLeft(37.09999682200002,36.416666666666664+0.6833333333333333));
}
