using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public sealed class MosaicElement : IImageElement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public SKRect Bounds { get; set; }
    public List<SKPoint> Points { get; } = new();
    public float StrokeWidth { get; set; } = 12f;
    public float Rotation { get; set; } = 0f;
    public SKBitmap? PixelatedPatch { get; set; }
    public int PatchLeft { get; set; } = 0;
    public int PatchTop { get; set; } = 0;

    public SKRect BoundingBox
    {
        get
        {
            if (Points.Count > 0)
            {
                float minX = float.MaxValue, minY = float.MaxValue;
                float maxX = float.MinValue, maxY = float.MinValue;
                foreach (var p in Points)
                {
                    if (p.X < minX) minX = p.X;
                    if (p.X > maxX) maxX = p.X;
                    if (p.Y < minY) minY = p.Y;
                    if (p.Y > maxY) maxY = p.Y;
                }
                float tol = Math.Max(StrokeWidth * 1.5f, 12f);
                return new SKRect(minX - tol, minY - tol, maxX + tol, maxY + tol);
            }
            return Bounds;
        }
    }

    public void Render(SKCanvas canvas)
    {
        if (PixelatedPatch == null || PixelatedPatch.Width <= 0 || PixelatedPatch.Height <= 0) return;

        canvas.Save();
        var box = BoundingBox;
        if (Math.Abs(Rotation) > 0.01f)
        {
            canvas.RotateDegrees(Rotation, box.MidX, box.MidY);
        }

        if (Points.Count > 1)
        {
            using var builder = new SKPathBuilder();
            builder.MoveTo(Points[0]);
            for (int i = 1; i < Points.Count; i++) builder.LineTo(Points[i]);
            using var strokePath = builder.Detach();

            using var strokePaint = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(StrokeWidth * 2f, 16f),
                StrokeCap = SKStrokeCap.Round,
                StrokeJoin = SKStrokeJoin.Round
            };
            using var fillBuilder = new SKPathBuilder();
            strokePaint.GetFillPath(strokePath, fillBuilder);
            using var fillPath = fillBuilder.Detach();

            canvas.ClipPath(fillPath, SKClipOperation.Intersect, antialias: true);
        }
        else
        {
            canvas.ClipRect(box, SKClipOperation.Intersect, antialias: true);
        }

        // 使用最近邻采样绘制绝对对齐的 Patch，确保每个马赛克方块边缘像素绝对锐利整洁，无双线性微移闪烁
        float drawX = (PatchLeft != 0 || PatchTop != 0) ? PatchLeft : box.Left;
        float drawY = (PatchLeft != 0 || PatchTop != 0) ? PatchTop : box.Top;
        canvas.DrawBitmap(PixelatedPatch, drawX, drawY, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        canvas.Restore();
    }

    public bool HitTest(SKPoint point)
    {
        var box = BoundingBox;
        if (Math.Abs(Rotation) > 0.01f)
        {
            point = GeometryHelper.RotatePoint(point, new SKPoint(box.MidX, box.MidY), -Rotation);
        }
        return box.Contains(point);
    }

    public void MoveBy(float dx, float dy)
    {
        Bounds = new SKRect(Bounds.Left + dx, Bounds.Top + dy, Bounds.Right + dx, Bounds.Bottom + dy);
        PatchLeft += (int)Math.Round(dx);
        PatchTop += (int)Math.Round(dy);
        for (int i = 0; i < Points.Count; i++)
        {
            Points[i] = new SKPoint(Points[i].X + dx, Points[i].Y + dy);
        }
    }

    public void Resize(SKRect newBounds)
    {
        Bounds = newBounds;
        if (Points.Count > 0)
        {
            var old = BoundingBox;
            if (old.Width > 0.001f && old.Height > 0.001f)
            {
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
        }
    }

    public IImageElement Clone()
    {
        var clone = new MosaicElement
        {
            Id = Guid.NewGuid(),
            Bounds = Bounds,
            StrokeWidth = StrokeWidth,
            Rotation = Rotation,
            PatchLeft = PatchLeft,
            PatchTop = PatchTop,
            PixelatedPatch = PixelatedPatch?.Copy()
        };
        clone.Points.AddRange(Points);
        return clone;
    }

    public static SKBitmap CreatePixelatedPatch(SKBitmap source, SKRect targetRect, int blockSize)
    {
        return CreatePixelatedPatch(source, targetRect, blockSize, out _, out _);
    }

    public static SKBitmap CreatePixelatedPatch(SKBitmap source, SKRect targetRect, int blockSize, out int alignedLeft, out int alignedTop)
    {
        blockSize = Math.Max(4, blockSize);

        // 1. 严格对齐全局网格坐标系，原点为 (0,0)
        // 这样无论鼠标如何微移，网格的分割线永不动摇，彻底消除 Y 轴/X 轴由于网格相位漂移产生的闪烁
        int gridX = (int)Math.Floor(targetRect.Left / blockSize) * blockSize;
        int gridY = (int)Math.Floor(targetRect.Top / blockSize) * blockSize;
        int gridRight = (int)Math.Ceiling(targetRect.Right / blockSize) * blockSize;
        int gridBottom = (int)Math.Ceiling(targetRect.Bottom / blockSize) * blockSize;

        // 限制在原图有效像素范围内
        int x = Math.Clamp(gridX, 0, source.Width);
        int y = Math.Clamp(gridY, 0, source.Height);
        int r = Math.Clamp(gridRight, 0, source.Width);
        int b = Math.Clamp(gridBottom, 0, source.Height);

        alignedLeft = x;
        alignedTop = y;

        int w = r - x;
        int h = b - y;
        if (w <= 0 || h <= 0)
        {
            return new SKBitmap(1, 1, source.ColorType, source.AlphaType);
        }

        var patch = new SKBitmap(w, h, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(patch);
        using var paint = new SKPaint { Style = SKPaintStyle.Fill, IsAntialias = false };

        // 2. 遍历该区域内每一个固定 blockSize x blockSize 的格子单元
        // 计算其在原图内的真实平均色彩，忠实还原周围色彩，杜绝偏色与五颜六色
        for (int cellY = y; cellY < b; cellY += blockSize)
        {
            int cellH = Math.Min(blockSize, b - cellY);
            for (int cellX = x; cellX < r; cellX += blockSize)
            {
                int cellW = Math.Min(blockSize, r - cellX);

                // 计算区域平均颜色 (高保真快速采样)
                long sumR = 0, sumG = 0, sumB = 0, sumA = 0;
                int sampleCount = 0;

                int step = Math.Max(1, blockSize / 4);
                for (int sy = cellY; sy < cellY + cellH; sy += step)
                {
                    for (int sx = cellX; sx < cellX + cellW; sx += step)
                    {
                        var color = source.GetPixel(sx, sy);
                        sumR += color.Red;
                        sumG += color.Green;
                        sumB += color.Blue;
                        sumA += color.Alpha;
                        sampleCount++;
                    }
                }

                if (sampleCount == 0)
                {
                    var color = source.GetPixel(cellX, cellY);
                    sumR = color.Red;
                    sumG = color.Green;
                    sumB = color.Blue;
                    sumA = color.Alpha;
                    sampleCount = 1;
                }

                byte avgR = (byte)(sumR / sampleCount);
                byte avgG = (byte)(sumG / sampleCount);
                byte avgB = (byte)(sumB / sampleCount);
                byte avgA = (byte)(sumA / sampleCount);

                paint.Color = new SKColor(avgR, avgG, avgB, avgA);
                canvas.DrawRect(new SKRect(cellX - x, cellY - y, cellX - x + cellW, cellY - y + cellH), paint);
            }
        }

        return patch;
    }
}
