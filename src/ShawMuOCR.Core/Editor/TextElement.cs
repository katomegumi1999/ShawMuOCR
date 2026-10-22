using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public sealed class TextElement : IImageElement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public float X { get; set; }
    public float Y { get; set; }
    public string Text { get; set; } = string.Empty;
    public SKColor Color { get; set; } = new SKColor(229, 57, 53);
    public float FontSize { get; set; } = 40f;
    public float StrokeWidth { get; set; } = 4f;
    public bool IsBold { get; set; } = true;
    public float Rotation { get; set; } = 0f;

    public SKRect BoundingBox
    {
        get
        {
            if (string.IsNullOrEmpty(Text))
            {
                return new SKRect(X - 4f, Y - 4f, X + 20f, Y + 24f);
            }

            var weight = StrokeWidth <= 2.5f ? SKFontStyleWeight.Normal :
                         (StrokeWidth <= 5f ? SKFontStyleWeight.SemiBold :
                         (StrokeWidth <= 10f ? SKFontStyleWeight.Bold : SKFontStyleWeight.ExtraBold));
            if (!IsBold) weight = SKFontStyleWeight.Normal;

            using var typeface = SKTypeface.FromFamilyName(
                "Microsoft YaHei",
                weight,
                SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright) ?? SKTypeface.Default;
            using var font = new SKFont(typeface, FontSize);

            var lines = Text.Split('\n');
            float maxW = 0f;
            float lineH = FontSize * 1.25f;

            foreach (var line in lines)
            {
                float w = font.MeasureText(line);
                if (w > maxW) maxW = w;
            }

            float strokeExtra = StrokeWidth > 2f ? (StrokeWidth - 2f) * 0.3f : 0f;
            float totalH = lines.Length * lineH;
            return new SKRect(X - 4f - strokeExtra, Y - 2f - strokeExtra, X + maxW + 8f + strokeExtra, Y + totalH + 4f + strokeExtra);
        }
    }

    public void Render(SKCanvas canvas)
    {
        if (string.IsNullOrEmpty(Text)) return;

        canvas.Save();
        var box = BoundingBox;
        if (Math.Abs(Rotation) > 0.01f)
        {
            canvas.RotateDegrees(Rotation, box.MidX, box.MidY);
        }

        var weight = StrokeWidth <= 2.5f ? SKFontStyleWeight.Normal :
                     (StrokeWidth <= 5f ? SKFontStyleWeight.SemiBold :
                     (StrokeWidth <= 10f ? SKFontStyleWeight.Bold : SKFontStyleWeight.ExtraBold));
        if (!IsBold) weight = SKFontStyleWeight.Normal;

        using var typeface = SKTypeface.FromFamilyName(
            "Microsoft YaHei",
            weight,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, FontSize);

        var lines = Text.Split('\n');
        float lineH = FontSize * 1.25f;
        float curY = Y;

        float strokeExtra = StrokeWidth > 2f ? (StrokeWidth - 2f) * 0.3f : 0f;

        using var fillPaint = new SKPaint
        {
            Color = Color,
            Style = strokeExtra > 0 ? SKPaintStyle.StrokeAndFill : SKPaintStyle.Fill,
            StrokeWidth = strokeExtra,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeCap = SKStrokeCap.Round,
            IsAntialias = true
        };

        foreach (var line in lines)
        {
            // Y 轴基线对齐：向下一个字符高度偏移行距
            float baselineY = curY + FontSize;
            canvas.DrawText(line, X, baselineY, SKTextAlign.Left, font, fillPaint);
            curY += lineH;
        }

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
        X += dx;
        Y += dy;
    }

    public void Resize(SKRect newBounds)
    {
        var old = BoundingBox;
        if (old.Width <= 0.001f || old.Height <= 0.001f) return;
        float scale = Math.Max(newBounds.Width / old.Width, newBounds.Height / old.Height);
        FontSize = Math.Max(5f, FontSize * scale);
        X = newBounds.Left;
        Y = newBounds.Top;
    }

    public IImageElement Clone()
    {
        return new TextElement
        {
            Id = Guid.NewGuid(),
            X = X,
            Y = Y,
            Text = Text,
            Color = Color,
            FontSize = FontSize,
            StrokeWidth = StrokeWidth,
            IsBold = IsBold,
            Rotation = Rotation
        };
    }
}
