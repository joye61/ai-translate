using AiTranslate.Desktop.Controls;
using Avalonia;
using Xunit;

namespace AiTranslate.Tests;

public sealed class SelectionPopupPlacementTests
{
    [Theory]
    [InlineData(0, 0, 1920, 1040, 1)]
    [InlineData(0, 0, 2560, 1392, 1.5)]
    [InlineData(-1920, -1080, 1920, 1040, 1.25)]
    [InlineData(80, 40, 1200, 760, 2)]
    public void GrowingCardStaysInsideWorkingArea(int x, int y, int width, int height, double scale)
    {
        var area = new PixelRect(x, y, width, height);
        var position = new PixelPoint(area.Right - 20, area.Bottom - 20);
        var maximum = SelectionPopupPlacement.MaximumSize(area, scale);
        foreach (var cardHeight in new[] { 160d, 280, 460 })
        {
            var size = new Size(Math.Min(420, maximum.Width), Math.Min(cardHeight, maximum.Height));
            position = SelectionPopupPlacement.Constrain(position, size, scale, area);
            Assert.True(area.Contains(new PixelRect(position, PixelSize.FromSize(size, scale))));
            Assert.InRange(position.X, area.X + 8, area.Right - (int)Math.Ceiling(size.Width * scale) - 8);
            Assert.InRange(position.Y, area.Y + 8, area.Bottom - (int)Math.Ceiling(size.Height * scale) - 8);
        }
    }

    [Theory]
    [InlineData(800, 600, 2)]
    [InlineData(1920, 1040, 1.5)]
    public void SmallWorkingAreasLimitCardSizeAndPreserveMargins(int width, int height, double scale)
    {
        var area = new PixelRect(-width, -height, width, height);
        var maximum = SelectionPopupPlacement.MaximumSize(area, scale);
        Assert.InRange(maximum.Width, 1, width / scale);
        Assert.InRange(maximum.Height, 1, 460);
        var position = SelectionPopupPlacement.Constrain(new PixelPoint(100, 100), maximum, scale, area);
        Assert.True(area.Contains(new PixelRect(position, PixelSize.FromSize(maximum, scale))));
        Assert.True(position.X + Math.Ceiling(maximum.Width * scale) <= area.Right - 8);
        Assert.True(position.Y + Math.Ceiling(maximum.Height * scale) <= area.Bottom - 8);
    }

    [Fact]
    public void ValidDraggedPositionIsPreservedAndNegativeOriginsAreRespected()
    {
        var area = new PixelRect(-1920, -1080, 1920, 1040);
        var position = new PixelPoint(-1600, -900);
        Assert.Equal(position, SelectionPopupPlacement.Constrain(position, new Size(420, 300), 1.5, area));
        Assert.Equal(new PixelPoint(-1912, -1072),
            SelectionPopupPlacement.Constrain(new PixelPoint(-2000, -1200), new Size(420, 300), 1.5, area));
    }
}
