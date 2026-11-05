using Fig.Core.Media;
using Fig.Core.Timeline;

namespace Fig.Core.Tests;

public class ReverseGeneratorTests
{
    /// <summary>How different the middle of two pictures is (where the timer's digit is; the rest is black).</summary>
    private static double Difference(DecodedFrame a, DecodedFrame b)
    {
        long sum = 0, n = 0;
        for (var y = a.Height * 3 / 10; y < a.Height * 7 / 10; y++)
            for (var x = a.Width * 4 / 10; x < a.Width * 6 / 10; x++)
                for (var c = 0; c < 3; c++, n++)
                    sum += Math.Abs(a.Pixels[(y * a.Width + x) * 4 + c] - b.Pixels[(y * b.Width + x) * 4 + c]);
        return sum / (double)Math.Max(1, n);
    }

    private static double Correlation(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        double ma = a.Take(n).Average(), mb = b.Take(n).Average(), num = 0, da = 0, db = 0;
        for (var i = 0; i < n; i++)
        {
            num += (a[i] - ma) * (b[i] - mb);
            da += (a[i] - ma) * (a[i] - ma);
            db += (b[i] - mb) * (b[i] - mb);
        }
        return num / Math.Sqrt(Math.Max(1e-12, da * db));
    }
}

public class ReverseEditTests
{
    [Fact]
    public void ReversingAVideo_PlaysItsCopyOverTheMirroredRange_AndBackAgain()
    {
        var (editor, track) = TimelineFixtures.Create();
        var original = new MediaAsset { Id = "src", DurationSec = 10 };
        var copy = new MediaAsset { Id = "rev", DurationSec = 10, ReverseOf = "src" };
        var assets = new[] { original, copy };
        editor.MediaResolver = id => assets.FirstOrDefault(a => a.Id == id);
        MediaAsset Counterpart(MediaAsset a) => a == original ? copy : original;

        var clip = TimelineFixtures.Video("v", 0, 3, srcIn: 2);   // seconds 2..5 of the file
        clip.SourceId = "src";
        editor.AddClip(track.Id, clip);

        Assert.True(editor.Reverse(["v"], Counterpart));
        Assert.Equal("rev", clip.SourceId);
        Assert.Equal((5.0, 8.0), (clip.SourceIn, clip.SourceOut));   // the same stretch, backwards: 10 - 5 .. 10 - 2

        Assert.True(editor.Reverse(["v"], Counterpart));
        Assert.Equal(("src", 2.0, 5.0), (clip.SourceId, clip.SourceIn, clip.SourceOut));

        editor.Undo();
        Assert.Equal(("rev", 5.0, 8.0), (clip.SourceId, clip.SourceIn, clip.SourceOut));
    }
}
