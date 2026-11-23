using Xunit;

namespace RedChat.Presentation.Tests;

public class VideoFrameGuardTests
{
    [Fact]
    public void ExactBuffer_IsAccepted() =>
        Assert.True(VideoFrameGuard.IsUsableBgr24(640 * 480 * 3, 640, 480, 640 * 3));

    [Fact]
    public void PaddedStride_WithEnoughBytes_IsAccepted() =>
        Assert.True(VideoFrameGuard.IsUsableBgr24(2000 * 480, 640, 480, 2000));

    [Fact]
    public void BufferTooShort_IsRejected() =>
        Assert.False(VideoFrameGuard.IsUsableBgr24(640 * 480 * 3 - 1, 640, 480, 640 * 3));

    [Fact]
    public void StrideSmallerThanOneRow_IsRejected() =>
        Assert.False(VideoFrameGuard.IsUsableBgr24(640 * 480 * 3, 640, 480, 640 * 3 - 1));

    [Theory]
    [InlineData(0u, 480u)]
    [InlineData(640u, 0u)]
    public void ZeroDimension_IsRejected(uint width, uint height) =>
        Assert.False(VideoFrameGuard.IsUsableBgr24(1_000_000, width, height, 1920));

    [Fact]
    public void AbsurdDimensions_AreRejectedWithoutOverflow() =>
        Assert.False(VideoFrameGuard.IsUsableBgr24(int.MaxValue, VideoFrameGuard.MaxSide + 1, 100, int.MaxValue));

    [Fact]
    public void NegativeStride_IsRejected() =>
        Assert.False(VideoFrameGuard.IsUsableBgr24(1_000_000, 100, 100, -300));

    [Fact]
    public void EmptyBuffer_IsRejected() =>
        Assert.False(VideoFrameGuard.IsUsableBgr24(0, 10, 10, 30));

    [Fact]
    public void LargeStrideTimesHeight_DoesNotOverflowInt() =>
        Assert.False(VideoFrameGuard.IsUsableBgr24(1_000, 8192, 8192, 8192 * 3));
}
