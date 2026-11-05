using Fig.Core.Media;

namespace Fig.Core.Tests;

public class PreviewFrameCacheTests
{
    private static DecodedFrame Frame(byte fill = 1)
        => new() { Width = 2, Height = 2, Pixels = new byte[] { fill, fill, fill, 255, fill, fill, fill, 255, fill, fill, fill, 255, fill, fill, fill, 255 } };

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
    public void MakeDistinct_DoesNotMutateCacheOwnedFrame()
    {
        var cache = new PreviewFrameCache();
        cache.Put("/a.mp4", 0, 2, 2, Frame(9));
        Assert.True(cache.TryGet("/a.mp4", 0, 2, 2, out var cached));
        var originalPixels = cached!.Pixels;
        var seen = new HashSet<byte[]> { originalPixels };
        var rented = new List<byte[]>();

        var distinct = FramePool.MakeDistinct(cached, seen, rented);

        Assert.NotSame(cached, distinct);
        Assert.Same(originalPixels, cached.Pixels);
        Assert.NotSame(originalPixels, distinct.Pixels);
        foreach (var pixels in rented)
            FramePool.Return(pixels);
        cache.Clear();
    }
}
