using SkiaSharp;

namespace ShawMuOCR.Core.Editor;

public interface IImageElement
{
    Guid Id { get; }
    SKRect BoundingBox { get; }
    void Render(SKCanvas canvas);
    bool HitTest(SKPoint point);
    void MoveBy(float dx, float dy);
    IImageElement Clone();
}
