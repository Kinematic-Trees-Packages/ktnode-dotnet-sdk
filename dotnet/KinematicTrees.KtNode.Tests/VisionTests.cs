using Xunit;

namespace KinematicTrees.KtNode.Tests;

public sealed class VisionTests
{
    [Fact]
    public void ImageFrameRoundTripsThroughImageSample()
    {
        var frame = new ImageFrame(
            "opencv-video-file",
            0,
            4,
            1,
            new byte[] { 0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3 },
            123);
        var payload = Vision.EncodeImageSample(frame);
        var summary = Vision.DecodeImageSampleSummary(payload, 12);
        Assert.Equal("opencv-video-file", summary.Source);
        Assert.Equal((ulong)0, summary.FrameNumber);
        Assert.Equal(new uint[] { 1, 4, 3 }, summary.DataShape);
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0, 1, 2, 0, 2, 3, 0, 3 }, summary.DataPrefix);
        Assert.Equal((ulong)123, summary.CapturedUnixNs);
    }

    [Fact]
    public void EncodeImageSampleRejectsInvalidShape()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Vision.EncodeImageSample(new ImageFrame("x", 0, 0, 1, Array.Empty<byte>(), 0)));
        Assert.Throws<ArgumentException>(() => Vision.EncodeImageSample(new ImageFrame("x", 0, 1, 1, new byte[] { 1, 2 }, 0)));
    }
}
