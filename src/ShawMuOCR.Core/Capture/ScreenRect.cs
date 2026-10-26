namespace ShawMuOCR.Core.Capture;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public static readonly ScreenRect Empty = default;

    public int Right => X + Width;
    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static ScreenRect FromPoints(int x1, int y1, int x2, int y2)
    {
        int minX = Math.Min(x1, x2);
        int minY = Math.Min(y1, y2);
        int maxX = Math.Max(x1, x2);
        int maxY = Math.Max(y1, y2);
        return new ScreenRect(minX, minY, maxX - minX, maxY - minY);
    }
}
