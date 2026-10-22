using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public sealed class RectangleElement : IImageElement
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

        canvas.DrawRect(rect, paint);
        canvas.Restore();
    }

    public bool HitTest(SKPoint point)
    {
        var box = BoundingBox;
        if (Math.Abs(Rotation) > 0.01f)
        {
            point = GeometryHelper.RotatePoint(point, new SKPoint(box.MidX, box.MidY), -Rotation);
        }

        float tol = Math.Max(StrokeWidth + 4f, 8f);

        var outer = new SKRect(box.Left - tol, box.Top - tol, box.Right + tol, box.Bottom + tol);
        if (!outer.Contains(point)) return false;

        if (box.Width <= tol * 2 || box.Height <= tol * 2)
        {
            return true;
        }

        var inner = new SKRect(box.Left + tol, box.Top + tol, box.Right - tol, box.Bottom - tol);
        return !inner.Contains(point);
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
        return new RectangleElement
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
