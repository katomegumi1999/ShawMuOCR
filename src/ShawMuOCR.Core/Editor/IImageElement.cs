using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public interface IImageElement
{
    Guid Id { get; }
    SKRect BoundingBox { get; }
    float Rotation { get; set; }
    void Render(SKCanvas canvas);
    bool HitTest(SKPoint point);
    void MoveBy(float dx, float dy);
    void Resize(SKRect newBounds);
    IImageElement Clone();
}
