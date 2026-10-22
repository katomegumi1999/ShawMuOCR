using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

/// <summary>
/// 椭圆/正圆图元 (支持按住 Shift 锁定 1:1 正圆)
/// </summary>
public sealed class EllipseElement : IImageElement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public float Left { get; set; }
    public float Top { get; set; }
    public float Right { get; set; }
    public float Bottom { get; set; }
    public SKColor Color { get; set; } = new SKColor(229, 57, 53); // Red
    public float StrokeWidth { get; set; } = 3f;
    public float Rotation { get; set; } = 0f;

    public SKRect BoundingBox => new(
        Math.Min(Left, Right),
        Math.Min(Top, Bottom),
        Math.Max(Left, Right),
        Math.Max(Top, Bottom));

    public void Render(SKCanvas canvas)
    {
        var rect = BoundingBox;
        if (rect.Width <= 0 || rect.Height <= 0) return;

        canvas.Save();
        if (Math.Abs(Rotation) > 0.01f)
        {
            var center = new SKPoint(rect.MidX, rect.MidY);
            canvas.RotateDegrees(Rotation, center.X, center.Y);
        }

        using var paint = new SKPaint
        {
            Color = Color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = StrokeWidth,
            IsAntialias = true,
            StrokeJoin = SKStrokeJoin.Round
        };

        canvas.DrawOval(rect, paint);
        canvas.Restore();
    }

    public bool HitTest(SKPoint point)
    {
        var box = BoundingBox;
        if (box.Width <= 0 || box.Height <= 0) return false;

        if (Math.Abs(Rotation) > 0.01f)
        {
            point = GeometryHelper.RotatePoint(point, new SKPoint(box.MidX, box.MidY), -Rotation);
        }

        float tol = Math.Max(StrokeWidth + 4f, 8f);
        var outer = new SKRect(box.Left - tol, box.Top - tol, box.Right + tol, box.Bottom + tol);
        if (!outer.Contains(point)) return false;

        float cx = box.MidX;
        float cy = box.MidY;
        float a = box.Width * 0.5f;
        float b = box.Height * 0.5f;

        if (a <= tol || b <= tol) return true;

        float dx = point.X - cx;
        float dy = point.Y - cy;

        // 椭圆方程归一化距离: (dx/a)^2 + (dy/b)^2
        // 外边界容差与内边界容差
        float outerA = a + tol;
        float outerB = b + tol;
        float innerA = Math.Max(1f, a - tol);
        float innerB = Math.Max(1f, b - tol);

        float distOuter = (dx * dx) / (outerA * outerA) + (dy * dy) / (outerB * outerB);
        float distInner = (dx * dx) / (innerA * innerA) + (dy * dy) / (innerB * innerB);

        return distOuter <= 1.0f && distInner >= 1.0f;
    }

    public void MoveBy(float dx, float dy)
    {
        Left += dx;
        Right += dx;
        Top += dy;
        Bottom += dy;
    }

    public void Resize(SKRect newBounds)
    {
        Left = newBounds.Left;
        Top = newBounds.Top;
        Right = newBounds.Right;
        Bottom = newBounds.Bottom;
    }

    public IImageElement Clone()
    {
        return new EllipseElement
        {
            Id = Guid.NewGuid(),
            Left = Left,
            Top = Top,
            Right = Right,
            Bottom = Bottom,
            Color = Color,
            StrokeWidth = StrokeWidth,
            Rotation = Rotation
        };
    }
}
