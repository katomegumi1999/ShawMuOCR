using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public sealed class PenElement : IImageElement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public List<SKPoint> Points { get; } = new();
    public SKColor Color { get; set; } = new SKColor(229, 57, 53);
    public float StrokeWidth { get; set; } = 4f;
    public float Rotation { get; set; } = 0f;

    public SKRect BoundingBox
    {
        get
        {
            if (Points.Count == 0) return SKRect.Empty;
            float minX = float.MaxValue, minY = float.MaxValue;
            float maxX = float.MinValue, maxY = float.MinValue;
            foreach (var p in Points)
            {
                if (p.X < minX) minX = p.X;
                if (p.X > maxX) maxX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.Y > maxY) maxY = p.Y;
            }
            float tol = Math.Max(StrokeWidth, 4f);
            return new SKRect(minX - tol, minY - tol, maxX + tol, maxY + tol);
        }
    }

    public void Render(SKCanvas canvas)
    {
        if (Points.Count < 2) return;

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
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        using var builder = new SKPathBuilder();
        builder.MoveTo(Points[0]);
        for (int i = 1; i < Points.Count; i++)
        {
            builder.LineTo(Points[i]);
        }
        using var path = builder.Detach();

        canvas.DrawPath(path, paint);
        canvas.Restore();
    }

    public bool HitTest(SKPoint point)
    {
        if (Points.Count < 2) return false;

        if (Math.Abs(Rotation) > 0.01f)
        {
            var box = BoundingBox;
            point = GeometryHelper.RotatePoint(point, new SKPoint(box.MidX, box.MidY), -Rotation);
        }

        float tol = Math.Max(StrokeWidth + 4f, 8f);
        for (int i = 0; i < Points.Count - 1; i++)
        {
            if (LineElement.DistanceToSegment(point, Points[i], Points[i + 1]) <= tol)
            {
                return true;
            }
        }
        return false;
    }

    public void MoveBy(float dx, float dy)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new SKPoint(Points[i].X + dx, Points[i].Y + dy);
        }
    }

    public void Resize(SKRect newBounds)
    {
        var old = BoundingBox;
        if (old.Width <= 0.001f || old.Height <= 0.001f) return;
        float scaleX = newBounds.Width / old.Width;
        float scaleY = newBounds.Height / old.Height;
        for (int i = 0; i < Points.Count; i++)
        {
            var p = Points[i];
            Points[i] = new SKPoint(
                newBounds.Left + (p.X - old.Left) * scaleX,
                newBounds.Top + (p.Y - old.Top) * scaleY);
        }
    }

    public IImageElement Clone()
    {
        var clone = new PenElement
        {
            Id = Guid.NewGuid(),
            Color = Color,
            StrokeWidth = StrokeWidth,
            Rotation = Rotation
        };
        clone.Points.AddRange(Points);
        return clone;
    }
}
