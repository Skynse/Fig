using Fig.Core.Media;

namespace Fig.Core.Tests;

public class PreviewFrameCacheTests
{
    private static DecodedFrame Frame(byte fill = 1)
        => new() { Width = 2, Height = 2, Pixels = new byte[] { fill, fill, fill, 255, fill, fill, fill, 255, fill, fill, fill, 255, fill, fill, fill, 255 } };

    [Fact]
    public void TryGet_MissThenHit_AfterPut()
    {
        var cache = new PreviewFrameCache(capacity: 8, bucketSec: 1.0 / 30.0);
        Assert.False(cache.TryGet("/a.mp4", 1.0, 640, 360, out _));

        cache.Put("/a.mp4", 1.0, 640, 360, Frame(7));
        Assert.True(cache.TryGet("/a.mp4", 1.0, 640, 360, out var hit));
        Assert.Equal(7, hit!.Pixels[0]);
    }

    [Fact]
    public void NearbyTimes_ShareBucket()
    {
        var cache = new PreviewFrameCache(capacity: 8, bucketSec: 1.0 / 30.0);
        cache.Put("/a.mp4", 1.00, 640, 360, Frame(3));
        // Within ~1/30s → same bucket
        Assert.True(cache.TryGet("/a.mp4", 1.01, 640, 360, out _));
    }

    [Fact]
    public void EvictsOldestWhenOverCapacity()
    {
        var cache = new PreviewFrameCache(capacity: 3, bucketSec: 1.0);
        cache.Put("/a.mp4", 0, 64, 36, Frame(1));
        cache.Put("/a.mp4", 1, 64, 36, Frame(2));
        cache.Put("/a.mp4", 2, 64, 36, Frame(3));
        cache.Put("/a.mp4", 3, 64, 36, Frame(4)); // evicts t=0

        Assert.False(cache.TryGet("/a.mp4", 0, 64, 36, out _));
        Assert.True(cache.TryGet("/a.mp4", 3, 64, 36, out _));
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void Clear_Empties()
    {
        var cache = new PreviewFrameCache();
        cache.Put("/a.mp4", 0, 64, 36, Frame());
        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.False(cache.TryGet("/a.mp4", 0, 64, 36, out _));
    }
}
