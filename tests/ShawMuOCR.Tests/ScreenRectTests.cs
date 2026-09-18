using ShawMuOCR.Core.Capture;
using Xunit;

namespace ShawMuOCR.Tests;

public class ScreenRectTests
{
    [Fact]
    public void FromPoints_NormalPoints_CalculatesCorrectBounds()
    {
        var rect = ScreenRect.FromPoints(10, 20, 100, 120);

        Assert.Equal(10, rect.X);
        Assert.Equal(20, rect.Y);
        Assert.Equal(90, rect.Width);
        Assert.Equal(100, rect.Height);
        Assert.Equal(100, rect.Right);
        Assert.Equal(120, rect.Bottom);
        Assert.False(rect.IsEmpty);
    }

    [Fact]
    public void FromPoints_ReversedPoints_CalculatesPositiveWidthHeight()
    {
        var rect = ScreenRect.FromPoints(100, 120, 10, 20);

        Assert.Equal(10, rect.X);
        Assert.Equal(20, rect.Y);
        Assert.Equal(90, rect.Width);
        Assert.Equal(100, rect.Height);
    }

    [Fact]
    public void FromPoints_NegativeCoordinates_HandlesCorrectly()
    {
        var rect = ScreenRect.FromPoints(-1920, 0, 0, 1080);

        Assert.Equal(-1920, rect.X);
        Assert.Equal(0, rect.Y);
        Assert.Equal(1920, rect.Width);
        Assert.Equal(1080, rect.Height);
    }
}
