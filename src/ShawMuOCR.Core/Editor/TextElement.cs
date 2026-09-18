using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public sealed class TextElement : IImageElement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public float X { get; set; }
    public float Y { get; set; }
    public string Text { get; set; } = string.Empty;
    public SKColor Color { get; set; } = new SKColor(229, 57, 53);
    public float FontSize { get; set; } = 22f;
    public bool IsBold { get; set; } = true;

    public SKRect BoundingBox
    {
        get
        {
            if (string.IsNullOrEmpty(Text))
            {
                return new SKRect(X - 4f, Y - 4f, X + 20f, Y + 24f);
            }

            using var typeface = SKTypeface.FromFamilyName(
                "Microsoft YaHei",
                IsBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
                SKFontStyleWidth.Normal,
                SKFontStyleSlant.Upright);
            using var font = new SKFont(typeface, FontSize);

            var lines = Text.Split('\n');
            float maxW = 0f;
            float lineH = FontSize * 1.25f;

            foreach (var line in lines)
            {
                float w = font.MeasureText(line);
                if (w > maxW) maxW = w;
            }

            float totalH = lines.Length * lineH;
            return new SKRect(X - 6f, Y - 4f, X + maxW + 12f, Y + totalH + 6f);
        }
    }

    public void Render(SKCanvas canvas)
    {
        if (string.IsNullOrEmpty(Text)) return;

        using var typeface = SKTypeface.FromFamilyName(
            "Microsoft YaHei",
            IsBold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright);
        using var font = new SKFont(typeface, FontSize);

        var lines = Text.Split('\n');
        float lineH = FontSize * 1.25f;
        float curY = Y;

        using var strokePaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2.5f,
            IsAntialias = true
        };

        using var fillPaint = new SKPaint
        {
            Color = Color,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        foreach (var line in lines)
        {
            // Y 轴基线对齐：向下一个字符高度偏移行距
            float baselineY = curY + FontSize;

            // 1. 细微黑边外发光，确保在黑白底色上都绝对清晰
            canvas.DrawText(line, X, baselineY, SKTextAlign.Left, font, strokePaint);
            // 2. 主体文字填充
            canvas.DrawText(line, X, baselineY, SKTextAlign.Left, font, fillPaint);

            curY += lineH;
        }
    }

    public bool HitTest(SKPoint point)
    {
        return BoundingBox.Contains(point);
    }

    public void MoveBy(float dx, float dy)
    {
        X += dx;
        Y += dy;
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
            IsBold = IsBold
        };
    }
}
