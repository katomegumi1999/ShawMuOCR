using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public sealed class ArrowElement : IImageElement
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
            float tol = Math.Max(StrokeWidth * 3f, 16f);
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

        float dx = EndX - StartX;
        float dy = EndY - StartY;
        float length = (float)Math.Sqrt(dx * dx + dy * dy);

        using var strokePaint = new SKPaint
        {
            Color = Color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = StrokeWidth,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round
        };

        if (length < 3f)
        {
            canvas.DrawLine(StartX, StartY, EndX, EndY, strokePaint);
            canvas.Restore();
            return;
        }

        float arrowLength = Math.Clamp(StrokeWidth * 5f, 14f, 32f);
        double angle = Math.Atan2(dy, dx);
        double leftAngle = angle + Math.PI - 0.45;
        double rightAngle = angle + Math.PI + 0.45;

        float w1X = EndX + (float)Math.Cos(leftAngle) * arrowLength;
        float w1Y = EndY + (float)Math.Sin(leftAngle) * arrowLength;
        float w2X = EndX + (float)Math.Cos(rightAngle) * arrowLength;
        float w2Y = EndY + (float)Math.Sin(rightAngle) * arrowLength;

        // 绘制箭头主线（略微往回缩短一点以无缝衔接箭头三角头部）
        float shorten = Math.Min(arrowLength * 0.6f, length * 0.5f);
        float shaftEndX = EndX - (dx / length) * shorten;
        float shaftEndY = EndY - (dy / length) * shorten;
        canvas.DrawLine(StartX, StartY, shaftEndX, shaftEndY, strokePaint);

        // 绘制填充箭头头部
        using var builder = new SKPathBuilder();
        builder.MoveTo(EndX, EndY);
        builder.LineTo(w1X, w1Y);
        builder.LineTo(w2X, w2Y);
        builder.Close();
        using var path = builder.Detach();

        using var fillPaint = new SKPaint
        {
            Color = Color,
            Style = SKPaintStyle.Fill,
            StrokeWidth = 1f,
            IsAntialias = true
        };
        canvas.DrawPath(path, fillPaint);
        canvas.Restore();
    }

    public bool HitTest(SKPoint point)
    {
        if (Math.Abs(Rotation) > 0.01f)
        {
            var box = BoundingBox;
            point = GeometryHelper.RotatePoint(point, new SKPoint(box.MidX, box.MidY), -Rotation);
        }

        float tol = Math.Max(StrokeWidth + 4f, 10f);
        if (LineElement.DistanceToSegment(point, new SKPoint(StartX, StartY), new SKPoint(EndX, EndY)) <= tol)
        {
            return true;
        }

        // 箭头头部附近容差检测
        float distToHead = (float)Math.Sqrt((point.X - EndX) * (point.X - EndX) + (point.Y - EndY) * (point.Y - EndY));
        return distToHead <= tol * 2f;
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
        return new ArrowElement
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
}
