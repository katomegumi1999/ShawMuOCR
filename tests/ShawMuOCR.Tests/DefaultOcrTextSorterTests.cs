using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Sorting;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class DefaultOcrTextSorterTests
{
    private readonly DefaultOcrTextSorter _sorter = new();

    [Fact]
    public void SortAndFormat_EmptyBlocks_ReturnsEmptyString()
    {
        var result = _sorter.SortAndFormat(Array.Empty<OcrTextBlock>());
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void SortAndFormat_SingleBlock_ReturnsText()
    {
        var blocks = new[]
        {
            new OcrTextBlock { Text = "Hello", BoundingBox = new SKRectI(10, 10, 50, 30) }
        };

        var result = _sorter.SortAndFormat(blocks);
        Assert.Equal("Hello", result);
    }

    [Fact]
    public void SortAndFormat_SameLine_SortsLeftToRight()
    {
        var blocks = new[]
        {
            new OcrTextBlock { Text = "World", BoundingBox = new SKRectI(60, 10, 110, 30) },
            new OcrTextBlock { Text = "Hello", BoundingBox = new SKRectI(10, 10, 55, 30) }
        };

        var result = _sorter.SortAndFormat(blocks);
        Assert.Equal("Hello World", result);
    }

    [Fact]
    public void SortAndFormat_MultiLine_SortsTopToBottomAndSeparatesWithNewline()
    {
        var blocks = new[]
        {
            new OcrTextBlock { Text = "Second Line", BoundingBox = new SKRectI(10, 50, 100, 75) },
            new OcrTextBlock { Text = "First Line", BoundingBox = new SKRectI(10, 10, 90, 35) }
        };

        var result = _sorter.SortAndFormat(blocks);
        var expected = $"First Line{Environment.NewLine}Second Line";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void SortAndFormat_CjkCharacters_DoNotAddSpace()
    {
        var blocks = new[]
        {
            new OcrTextBlock { Text = "小沐", BoundingBox = new SKRectI(10, 10, 40, 30) },
            new OcrTextBlock { Text = "工具", BoundingBox = new SKRectI(42, 10, 75, 30) }
        };

        var result = _sorter.SortAndFormat(blocks);
        Assert.Equal("小沐工具", result);
    }
}
