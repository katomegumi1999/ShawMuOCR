using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

/// <summary>
/// 全图防伪与版权水印图元 (支持根据图像尺寸按比例自适应字号与平铺密度，支持平铺或单条居中)
/// </summary>
public sealed class WatermarkElement : IImageElement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Text { get; set; } = "内部资料 严禁外传";
    public SKColor Color { get; set; } = new SKColor(160, 160, 160);

    /// <summary>
    /// 水印字号。若为 0（默认），则自动根据图片尺寸 (ImageWidth, ImageHeight) 按比例缩放字号与平铺密度；
    /// 若 > 0 则使用指定字号。
    /// </summary>
    public float FontSize { get; set; } = 0f;

    /// <summary>
    /// 水印平铺密集度倍率（默认 1.0f，值越大水印越密集，间距越紧凑）。
    /// </summary>
    public float Density { get; set; } = 1.0f;

    public float Opacity { get; set; } = 0.25f;
    public float Rotation { get; set; } = -28f;
    public bool IsTiled { get; set; } = true;
    public int ImageWidth { get; set; } = 800;
    public int ImageHeight { get; set; } = 600;

    public SKRect BoundingBox => new(0, 0, ImageWidth, ImageHeight);

    /// <summary>
    /// 获取当前生效的水印字号（支持根据图片对角线尺寸自适应按比例缩放）
    /// </summary>
    public float GetEffectiveFontSize()
    {
        if (FontSize > 0)
        {
            return FontSize;
        }

        double diag = Math.Sqrt((double)ImageWidth * ImageWidth + (double)ImageHeight * ImageHeight);
        if (diag < 10) diag = 1000;

        if (IsTiled)
        {
            // 密集平铺模式：字号随图像对角线按约 2.2% 比例平滑缩放，
            // 确保小图字体轻盈不拥挤、4K超清大图文字清晰不细碎（约束在 14~160 之间）
            return (float)Math.Clamp(diag * 0.022, 14.0, 160.0);
        }
        else
        {
            // 单条居中模式：单条醒目标记，占对角线约 4.5%
            float size = (float)Math.Clamp(diag * 0.045, 18.0, 240.0);
            if (!string.IsNullOrEmpty(Text))
            {
                // 若文字长度较长，动态约束保证文字宽度不超过画布宽度的 85%
                float approxWidth = Text.Length * size;
                float maxWidth = Math.Max(ImageWidth * 0.85f, 100f);
                if (approxWidth > maxWidth)
                {
                    size = Math.Max(16f, size * (maxWidth / approxWidth));
                }
            }
            return size;
        }
    }

    public void Render(SKCanvas canvas)
    {
        if (string.IsNullOrEmpty(Text)) return;

        canvas.Save();

        byte alpha = (byte)Math.Clamp((int)(Opacity * (Color.Alpha / 255f) * 255), 10, 255);
        var paintColor = new SKColor(Color.Red, Color.Green, Color.Blue, alpha);

        float effectiveFontSize = GetEffectiveFontSize();

        using var typeface = SKTypeface.FromFamilyName(
            "Microsoft YaHei",
            SKFontStyleWeight.Bold,
            SKFontStyleWidth.Normal,
            SKFontStyleSlant.Upright) ?? SKTypeface.Default;
        using var font = new SKFont(typeface, effectiveFontSize);
        using var paint = new SKPaint
        {
            Color = paintColor,
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        float textWidth = font.MeasureText(Text);
        float textHeight = effectiveFontSize * 1.25f;

        // 计算文字垂直几何居中偏移，使文字基线与旋转轴完全垂直对称
        var metrics = font.Metrics;
        float midOffset = -(metrics.Ascent + metrics.Descent) / 2f;

        if (IsTiled)
        {
            double rad = Math.Abs(Rotation) * Math.PI / 180.0;
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);
            float projW = textWidth * cos + textHeight * sin;
            float projH = textWidth * sin + textHeight * cos;

            // 平铺密度调节：间距随字号与文字旋转外接框等比例缩放，并结合手动密集度倍率 (Density)
            // Density > 1.0 间距变小更密集，Density < 1.0 间距变大更稀疏
            float spacingFactor = 1.0f / Math.Clamp(Density, 0.2f, 5.0f);
            float gapX = Math.Max(effectiveFontSize * 2.2f, projW * 0.35f) * spacingFactor;
            float gapY = Math.Max(effectiveFontSize * 1.8f, projH * 0.40f) * spacingFactor;

            float stepX = projW + gapX;
            float stepY = projH + gapY;

            // 仅遍历与可视区域相交的网格，消除多余绘制并极大提升 4K 画布性能
            int row = 0;
            for (float y = -stepY; y <= ImageHeight + stepY; y += stepY, row++)
            {
                float offsetX = (row % 2 == 0) ? 0f : (stepX * 0.5f);
                for (float x = -stepX + offsetX; x <= ImageWidth + stepX; x += stepX)
                {
                    canvas.Save();
                    canvas.RotateDegrees(Rotation, x, y);
                    canvas.DrawText(Text, x, y + midOffset, SKTextAlign.Center, font, paint);
                    canvas.Restore();
                }
            }
        }
        else
        {
            float cx = ImageWidth / 2f;
            float cy = ImageHeight / 2f;
            canvas.Save();
            canvas.RotateDegrees(Rotation, cx, cy);
            canvas.DrawText(Text, cx, cy + midOffset, SKTextAlign.Center, font, paint);
            canvas.Restore();
        }

        canvas.Restore();
    }

    public bool HitTest(SKPoint point)
    {
        if (string.IsNullOrEmpty(Text)) return false;
        if (point.X < 0 || point.X > ImageWidth || point.Y < 0 || point.Y > ImageHeight) return false;

        float effectiveFontSize = GetEffectiveFontSize();
        using var typeface = SKTypeface.FromFamilyName("Microsoft YaHei") ?? SKTypeface.Default;
        using var font = new SKFont(typeface, effectiveFontSize);
        float textWidth = font.MeasureText(Text);
        float halfW = textWidth * 0.5f + 10f;
        float halfH = effectiveFontSize * 0.75f + 10f;

        if (IsTiled)
        {
            float textHeight = effectiveFontSize * 1.25f;
            double rad = Math.Abs(Rotation) * Math.PI / 180.0;
            float cos = (float)Math.Cos(rad);
            float sin = (float)Math.Sin(rad);
            float projW = textWidth * cos + textHeight * sin;
            float projH = textWidth * sin + textHeight * cos;

            float spacingFactor = 1.0f / Math.Clamp(Density, 0.2f, 5.0f);
            float gapX = Math.Max(effectiveFontSize * 2.2f, projW * 0.35f) * spacingFactor;
            float gapY = Math.Max(effectiveFontSize * 1.8f, projH * 0.40f) * spacingFactor;

            float stepX = projW + gapX;
            float stepY = projH + gapY;

            int row = 0;
            for (float y = -stepY; y <= ImageHeight + stepY; y += stepY, row++)
            {
                float offsetX = (row % 2 == 0) ? 0f : (stepX * 0.5f);
                for (float x = -stepX + offsetX; x <= ImageWidth + stepX; x += stepX)
                {
                    if (Math.Abs(point.X - x) <= halfW * 1.5f && Math.Abs(point.Y - y) <= halfH * 1.5f)
                    {
                        var unrot = GeometryHelper.RotatePoint(point, new SKPoint(x, y), -Rotation);
                        if (Math.Abs(unrot.X - x) <= halfW && Math.Abs(unrot.Y - y) <= halfH)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        else
        {
            float cx = ImageWidth / 2f;
            float cy = ImageHeight / 2f;
            var unrot = GeometryHelper.RotatePoint(point, new SKPoint(cx, cy), -Rotation);
            return Math.Abs(unrot.X - cx) <= halfW && Math.Abs(unrot.Y - cy) <= halfH;
        }
    }

    public void MoveBy(float dx, float dy)
    {
        // 全局水印平铺覆盖整张画布
    }

    public void Resize(SKRect newBounds)
    {
        ImageWidth = (int)Math.Max(10, newBounds.Width);
        ImageHeight = (int)Math.Max(10, newBounds.Height);
    }

    public IImageElement Clone()
    {
        return new WatermarkElement
        {
            Id = Id,
            Text = Text,
            Color = Color,
            FontSize = FontSize,
            Density = Density,
            Opacity = Opacity,
            Rotation = Rotation,
            IsTiled = IsTiled,
            ImageWidth = ImageWidth,
            ImageHeight = ImageHeight
        };
    }
}
