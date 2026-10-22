using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public static class GeometryHelper
{
    public static SKPoint RotatePoint(SKPoint pt, SKPoint center, float degrees)
    {
        if (Math.Abs(degrees) < 0.001f) return pt;
        float rad = degrees * (float)(Math.PI / 180.0);
        float cos = (float)Math.Cos(rad);
        float sin = (float)Math.Sin(rad);
        float dx = pt.X - center.X;
        float dy = pt.Y - center.Y;
        return new SKPoint(center.X + dx * cos - dy * sin, center.Y + dx * sin + dy * cos);
    }
}
