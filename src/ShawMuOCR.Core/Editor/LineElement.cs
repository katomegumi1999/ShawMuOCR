using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public sealed class LineElement : IImageElement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public float StartX { get; set; }
    public float StartY { get; set; }
    public float EndX { get; set; }
    public float EndY { get; set; }
    public SKColor Color { get; set; } = new SKColor(229, 57, 53);
    public float StrokeWidth { get; set; } = 3f;
    public float Rotation { get; set; } = 0f;

    public SKRect BoundingBox
    {
        get
        {
            float tol = Math.Max(StrokeWidth, 4f);
            return new SKRect(
                Math.Min(StartX, EndX) - tol,
                Math.Min(StartY, EndY) - tol,
                Math.Max(StartX, EndX) + tol,
                Math.Max(StartY, EndY) + tol);
        }
    }

    public void Render(SKCanvas canvas)
    {
        canvas.Save();
        if (Math.Abs(Rotation) > 0.01f)
        {
            var box = BoundingBox;
            canvas.RotateDegrees(Rotation, box.MidX, box.MidY);
        }

        using var paint = new SKPaint
        {
            Color = Color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = StrokeWidth,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };

        canvas.DrawLine(StartX, StartY, EndX, EndY, paint);
        canvas.Restore();
    }

    public bool HitTest(SKPoint point)
    {
        if (Math.Abs(Rotation) > 0.01f)
        {
            var box = BoundingBox;
            point = GeometryHelper.RotatePoint(point, new SKPoint(box.MidX, box.MidY), -Rotation);
        }

        float tol = Math.Max(StrokeWidth + 4f, 8f);
        return DistanceToSegment(point, new SKPoint(StartX, StartY), new SKPoint(EndX, EndY)) <= tol;
    }

    public void MoveBy(float dx, float dy)
    {
        StartX += dx;
        EndX += dx;
        StartY += dy;
        EndY += dy;
    }

    public void Resize(SKRect newBounds)
    {
        var old = BoundingBox;
        if (old.Width <= 0.001f || old.Height <= 0.001f)
        {
            StartX = newBounds.Left;
            StartY = newBounds.Top;
            EndX = newBounds.Right;
            EndY = newBounds.Bottom;
            return;
        }

        float scaleX = newBounds.Width / old.Width;
        float scaleY = newBounds.Height / old.Height;
        StartX = newBounds.Left + (StartX - old.Left) * scaleX;
        EndX = newBounds.Left + (EndX - old.Left) * scaleX;
        StartY = newBounds.Top + (StartY - old.Top) * scaleY;
        EndY = newBounds.Top + (EndY - old.Top) * scaleY;
    }

    public IImageElement Clone()
    {
        return new LineElement
        {
            Id = Guid.NewGuid(),
            StartX = StartX,
            StartY = StartY,
            EndX = EndX,
            EndY = EndY,
            Color = Color,
            StrokeWidth = StrokeWidth,
            Rotation = Rotation
        };
    }

    public static float DistanceToSegment(SKPoint p, SKPoint a, SKPoint b)
    {
        float l2 = (b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y);
        if (l2 == 0) return (float)Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));

        float t = Math.Clamp(((p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y)) / l2, 0f, 1f);
        float projX = a.X + t * (b.X - a.X);
        float projY = a.Y + t * (b.Y - a.Y);
        return (float)Math.Sqrt((p.X - projX) * (p.X - projX) + (p.Y - projY) * (p.Y - projY));
    }
}
