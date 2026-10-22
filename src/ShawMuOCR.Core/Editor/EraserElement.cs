using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

/// <summary>
/// 笔刷擦除图元 (使用 Clear 混合模式直接擦除已添加的标注像素，还原底层原图，支持粗细调节)
/// </summary>
public sealed class EraserElement : IImageElement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public List<SKPoint> Points { get; } = new();
    public float StrokeWidth { get; set; } = 14f;
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
            float r = StrokeWidth * 0.5f;
            return new SKRect(minX - r, minY - r, maxX + r, maxY + r);
        }
    }

    public void Render(SKCanvas canvas)
    {
        if (Points.Count == 0) return;

        if (Points.Count == 1)
        {
            using var fillPaint = new SKPaint
            {
                BlendMode = SKBlendMode.Clear,
                Style = SKPaintStyle.Fill,
                IsAntialias = true
            };
            canvas.DrawCircle(Points[0], StrokeWidth * 0.5f, fillPaint);
            return;
        }

        using var paint = new SKPaint
        {
            BlendMode = SKBlendMode.Clear,
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
    }

    public bool HitTest(SKPoint point) => false;

    public void MoveBy(float dx, float dy)
    {
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new SKPoint(Points[i].X + dx, Points[i].Y + dy);
        }
    }

    public void Resize(SKRect newBounds) { }

    public IImageElement Clone()
    {
        var clone = new EraserElement
        {
            Id = Id,
            StrokeWidth = StrokeWidth,
            Rotation = Rotation
        };
        clone.Points.AddRange(Points);
        return clone;
    }
}
